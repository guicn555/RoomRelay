using System.Runtime.InteropServices;
using Serilog;
using Windows.Win32.Media.Audio;
using WinAudioClient = Windows.Win32.Media.Audio.IAudioClient;

namespace SonosStreaming.Core.Audio;

// Captures the default Windows render endpoint via WASAPI loopback.
public sealed unsafe class WasapiLoopbackSource : WasapiCaptureBase
{
    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    [DllImport("Ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    private readonly int _captureBufferMs;

    public WasapiLoopbackSource(int captureBufferMs = 200)
    {
        _captureBufferMs = captureBufferMs;
    }

    // Allow querying the mix format before Start() is called.
    public new MixFormat MixFormat => _mixFormat ?? ProbeMixFormat();

    public void Start()
    {
        int hr = CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_IMMDeviceEnumerator, out var pEnum);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pEnum);
        Marshal.Release(pEnum);

        enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
        Marshal.ReleaseComObject(enumerator);

        var iidAudioClient = IID_IAudioClient;
        device.Activate(&iidAudioClient, Windows.Win32.System.Com.CLSCTX.CLSCTX_INPROC_SERVER, null, out var audioClientObj);
        Marshal.ReleaseComObject(device);
        var client = (WinAudioClient)audioClientObj;

        WAVEFORMATEX* pwfx;
        client.GetMixFormat(&pwfx);
        try
        {
            long hnsBufferDuration = _captureBufferMs * 10_000L;
            client.Initialize(
                AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                hnsBufferDuration,
                0,
                pwfx,
                null);

            BeginCapture(client, pwfx, "WasapiLoopback");

            Log.Information("WASAPI loopback capture started: {Rate} Hz, {Ch} ch, {Bits}-bit, float={IsFloat}, buffer={BufferMs} ms",
                _mixFormat!.SampleRate, _mixFormat!.Channels, _mixFormat!.BitsPerSample, _mixFormat!.IsFloat, _captureBufferMs);
        }
        finally
        {
            Marshal.FreeCoTaskMem((IntPtr)pwfx);
        }
    }

    // Idle gaps used to be filled by a dedicated silence thread here, which ran
    // on a Thread.Sleep(100) cadence but emitted exactly 100 ms of audio per
    // wake-up, so every idle stretch under-delivered by the sleep overshoot
    // (issue #26). It also raced the pump's own silence injection, producing
    // double silence when both fired. PipelineRunner's rate governor is now the
    // single source of padding, driven off a wall clock.

    protected override unsafe void ConvertToFloat(byte* src, float[] dst, uint frames)
    {
        int totalSamples = (int)frames * _channelCount;
        if (_inputIsFloat && _bytesPerSample == 4)
        {
            fixed (float* d = dst) Buffer.MemoryCopy(src, d, totalSamples * 4, totalSamples * 4);
        }
        else if (!_inputIsFloat && _bytesPerSample == 2)
        {
            short* s = (short*)src;
            const float scale = 1f / 32768f;
            for (int i = 0; i < totalSamples; i++) dst[i] = s[i] * scale;
        }
        else if (!_inputIsFloat && _bytesPerSample == 4)
        {
            int* s = (int*)src;
            const float scale = 1f / 2147483648f;
            for (int i = 0; i < totalSamples; i++) dst[i] = s[i] * scale;
        }
        else
        {
            Log.Warning("Unsupported mix format: float={F}, bytesPerSample={B}", _inputIsFloat, _bytesPerSample);
        }
    }

    protected override void OnDispose()
    {
        Log.Information("WASAPI loopback capture stopped");
    }

    // Reads the device mix format blind (no IAudioClient lifetime required)
    // so callers can query MixFormat before Start.
    internal static unsafe MixFormat ProbeMixFormat()
    {
        int hr = CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_IMMDeviceEnumerator, out var pEnum);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pEnum);
        Marshal.Release(pEnum);
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            try
            {
                var iid = IID_IAudioClient;
                device.Activate(&iid, Windows.Win32.System.Com.CLSCTX.CLSCTX_INPROC_SERVER, null, out var clientObj);
                var client = (WinAudioClient)clientObj;
                try
                {
                    WAVEFORMATEX* pwfx;
                    client.GetMixFormat(&pwfx);
                    try { return DecodeWaveFormat(pwfx); }
                    finally { Marshal.FreeCoTaskMem((IntPtr)pwfx); }
                }
                finally { Marshal.ReleaseComObject(client); }
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }
}
