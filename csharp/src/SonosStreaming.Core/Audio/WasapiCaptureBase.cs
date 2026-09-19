using System.Runtime.InteropServices;
using System.Threading.Channels;
using Serilog;
using Windows.Win32.Media.Audio;
using WinAudioClient = Windows.Win32.Media.Audio.IAudioClient;
using WinAudioCapture = Windows.Win32.Media.Audio.IAudioCaptureClient;

namespace SonosStreaming.Core.Audio;

// Template-method base for WASAPI capture sources. Factors out the common
// event-driven capture loop, NextFrameAsync, and disposal plumbing while
// letting each derived class handle its own activation / format / conversion.
public abstract class WasapiCaptureBase : IAudioSource
{
    protected const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    protected const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint COINIT_MULTITHREADED = 0;

    [DllImport("Ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint dwCoInit);

    [DllImport("Ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    // MMCSS. A plain background thread competes with every other thread on the
    // box, so under load it gets descheduled, leaves packets sitting in the
    // WASAPI buffer, and then drains several periods in one oversized burst.
    // Those bursts push the stream ahead of the wall clock and make the rate
    // governor trim real audio. "Pro Audio" is the scheduling class Windows
    // provides for exactly this.
    [DllImport("Avrt.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref uint taskIndex);

    [DllImport("Avrt.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);

    protected readonly Channel<PcmFrameF32> _channel;
    protected MixFormat? _mixFormat;
    private WinAudioClient? _audioClient;
    private WinAudioCapture? _captureClient;
    private EventWaitHandle? _bufferEvent;
    private Thread? _captureThread;
    protected volatile bool _stopped;
    protected int _channelCount;
    protected int _bytesPerSample;
    protected bool _inputIsFloat;
    private readonly int _capacity;
    private long _droppedBuffers;
    private long _droppedSampleFrames;

    // Capture queue depth. Eight buffers was roughly 80 ms at a typical 10 ms
    // device period, so any pump hiccup discarded audio and silently shortened
    // the stream (issue #26). Queue about a second instead.
    public const int DefaultCapacity = 96;

    protected WasapiCaptureBase(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(2, capacity);
        _channel = Channel.CreateBounded<PcmFrameF32>(new BoundedChannelOptions(_capacity)
        {
            // Live audio: if the pump falls behind, the oldest buffer is the one
            // worth losing, since keeping it would only add latency. Drops are
            // counted so the rate governor and diagnostics can see them.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public MixFormat MixFormat => _mixFormat ?? throw new InvalidOperationException("Capture not started");

    /// <summary>Capture buffers discarded because the pump could not keep up.</summary>
    public long DroppedBuffers => Interlocked.Read(ref _droppedBuffers);

    /// <summary>Sample frames (per channel) lost to those discarded buffers.</summary>
    public long DroppedSampleFrames => Interlocked.Read(ref _droppedSampleFrames);

    // TryWrite always succeeds under DropOldest, so a drop can only be spotted
    // by checking the queue depth first. This is a diagnostic counter, so the
    // small race against the reader is acceptable.
    protected void WriteFrame(PcmFrameF32 frame)
    {
        if (_channel.Reader.Count >= _capacity)
        {
            Interlocked.Increment(ref _droppedBuffers);
            Interlocked.Add(ref _droppedSampleFrames, frame.FrameCount);
        }
        _channel.Writer.TryWrite(frame);
    }

    // Derived class calls this after it has activated and initialized its
    // IAudioClient and determined the mix format.
    internal unsafe void BeginCapture(WinAudioClient client, WAVEFORMATEX* format, string threadName)
    {
        _audioClient = client;
        _mixFormat = DecodeWaveFormat(format);
        _channelCount = format->nChannels;
        _bytesPerSample = format->wBitsPerSample / 8;
        _inputIsFloat = _mixFormat.IsFloat;

        _bufferEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        _audioClient.SetEventHandle(new Windows.Win32.Foundation.HANDLE(_bufferEvent.SafeWaitHandle.DangerousGetHandle()));

        var captureGuid = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"); // IID_IAudioCaptureClient
        _audioClient.GetService(&captureGuid, out var captureObj);
        _captureClient = (WinAudioCapture)captureObj;

        OnBeforeCaptureStart();

        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = threadName };
        _captureThread.Start();

        _audioClient.Start();
    }

    protected virtual void OnBeforeCaptureStart() { }

    private void CaptureLoop()
    {
        CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        var mmcss = JoinProAudioClass();
        try
        {
            while (!_stopped)
            {
                if (!_bufferEvent!.WaitOne(200)) continue;
                while (true)
                {
                    uint packetFrames;
                    try { _captureClient!.GetNextPacketSize(out packetFrames); }
                    catch (Exception ex) { Log.Warning(ex, "GetNextPacketSize failed"); return; }
                    if (packetFrames == 0) break;

                    unsafe
                    {
                        byte* data;
                        uint framesRead;
                        uint flags;
                        _captureClient.GetBuffer(&data, out framesRead, out flags, null, null);
                        try
                        {
                            if (framesRead == 0) break;
                            var samples = new float[framesRead * _channelCount];
                            if ((flags & (uint)Windows.Win32.Media.Audio._AUDCLNT_BUFFERFLAGS.AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != null)
                            {
                                ConvertToFloat(data, samples, framesRead);
                            }
                            WriteFrame(new PcmFrameF32(samples, _mixFormat!.SampleRate, (ushort)_channelCount));
                        }
                        finally { _captureClient.ReleaseBuffer(framesRead); }
                    }
                }
            }
        }
        finally
        {
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
            CoUninitialize();
        }
    }

    // Best effort: MMCSS can be unavailable (disabled by policy, or the task
    // profile missing), and capture still works without it, just with more
    // jitter under load.
    private static IntPtr JoinProAudioClass()
    {
        try
        {
            uint taskIndex = 0;
            var handle = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
            if (handle == IntPtr.Zero)
                Log.Debug("MMCSS Pro Audio unavailable (error {Err}); capture runs at normal priority",
                    Marshal.GetLastWin32Error());
            return handle;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not join the MMCSS Pro Audio class");
            return IntPtr.Zero;
        }
    }

    protected abstract unsafe void ConvertToFloat(byte* src, float[] dst, uint frames);

    public Task<PcmFrameF32?> NextFrameAsync(CancellationToken ct) => NextFrameInner(ct);

    private async Task<PcmFrameF32?> NextFrameInner(CancellationToken ct)
    {
        if (_stopped) return null;
        try { return await _channel.Reader.ReadAsync(ct).ConfigureAwait(false); }
        catch (ChannelClosedException) { return null; }
    }

    public void Shutdown()
    {
        if (_stopped) return;
        _stopped = true;
        try { _audioClient?.Stop(); } catch { }
        _channel.Writer.TryComplete();
        _bufferEvent?.Set();
    }

    public void Dispose()
    {
        Shutdown();
        try { _captureThread?.Join(500); } catch { }
        OnDispose();
        if (_captureClient != null) { Marshal.ReleaseComObject(_captureClient); _captureClient = null; }
        if (_audioClient != null) { Marshal.ReleaseComObject(_audioClient); _audioClient = null; }
        _bufferEvent?.Dispose();
        _bufferEvent = null;
    }

    protected virtual void OnDispose() { }

    internal static unsafe MixFormat DecodeWaveFormat(WAVEFORMATEX* p)
    {
        int rate = (int)p->nSamplesPerSec;
        int channels = p->nChannels;
        int bits = p->wBitsPerSample;
        bool isFloat;
        const int WAVE_FORMAT_EXTENSIBLE = unchecked((int)0xFFFE);
        const int WAVE_FORMAT_IEEE_FLOAT = 0x0003;
        if (p->wFormatTag == WAVE_FORMAT_EXTENSIBLE && p->cbSize >= 22)
        {
            var ext = (WAVEFORMATEXTENSIBLE*)p;
            isFloat = ext->SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        }
        else
        {
            isFloat = p->wFormatTag == WAVE_FORMAT_IEEE_FLOAT;
        }
        return new MixFormat((uint)rate, (ushort)channels, (ushort)bits, isFloat);
    }
}
