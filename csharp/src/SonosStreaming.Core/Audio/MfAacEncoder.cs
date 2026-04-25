using System.Runtime.InteropServices;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace SonosStreaming.Core.Audio;

// AAC-LC encoder backed by the Windows Media Foundation AAC encoder MFT
// (CLSID_CMSAACEncMFT). Ships with Windows 7+, so there is no third-party
// codec dependency at runtime. Configured to emit ADTS-framed output
// directly (MF_MT_AAC_PAYLOAD_TYPE = 1), which is what Sonos expects over
// HTTP — no manual ADTS wrap needed.
public sealed unsafe class MfAacEncoder : IAudioEncoder
{
    private const int TargetSampleRate = 48000;
    private const int TargetChannels = 2;
    private const int TargetBitsPerSample = 16;
    private const int AacFrameSize = 1024;     // samples per channel

    private readonly int _targetBitRate;

    private const uint MF_VERSION = 0x00020070;
    private const uint MFSTARTUP_FULL = 0;
    private const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    private const uint MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    private const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const int S_OK = 0;

    private static readonly Guid CLSID_CMSAACEncMFT = new("93AF0C51-2275-45D2-A35B-F2BA21CAED00");

    [DllImport("Ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    private IMFTransform? _transform;
    private readonly short[] _accumulator = new short[AacFrameSize * TargetChannels];
    private int _accumulatedSamples;
    private long _sampleCount;
    private bool _mfStarted;
    private bool _disposed;

    // Batch buffer: accumulated ADTS frames are copied here so we emit
    // fewer, larger chunks instead of one byte[] per AAC frame.
    private byte[] _batchBuffer = new byte[16384];
    private int _batchOffset;

    public MfAacEncoder(int targetBitRate = 256_000)
    {
        _targetBitRate = targetBitRate;
        var hr = PInvoke.MFStartup(MF_VERSION, MFSTARTUP_FULL);
        if (hr.Failed) throw Marshal.GetExceptionForHR(hr.Value)!;
        _mfStarted = true;

        const uint CLSCTX_INPROC_SERVER = 0x1;
        var iidIMFTransform = typeof(IMFTransform).GUID;
        int ccHr = CoCreateInstance(CLSID_CMSAACEncMFT, IntPtr.Zero, CLSCTX_INPROC_SERVER, iidIMFTransform, out var pTransform);
        if (ccHr < 0) throw Marshal.GetExceptionForHR(ccHr)!;
        _transform = (IMFTransform)Marshal.GetObjectForIUnknown(pTransform);
        Marshal.Release(pTransform);

        ConfigureTypes();
        _transform.ProcessMessage((MFT_MESSAGE_TYPE)MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);

        Log.Information("MF AAC encoder ready: {Rate} Hz, {Ch} ch, {Bitrate} bps",
            TargetSampleRate, TargetChannels, _targetBitRate);
    }

    private void ConfigureTypes()
    {
        // Output type (AAC) must be set before input (PCM) on this MFT.
        PInvoke.MFCreateMediaType(out var outType).ThrowOnFailure();
        outType.SetGUID(MfAttr.MF_MT_MAJOR_TYPE, MfAttr.MFMediaType_Audio);
        outType.SetGUID(MfAttr.MF_MT_SUBTYPE, MfAttr.MFAudioFormat_AAC);
        outType.SetUINT32(MfAttr.MF_MT_AUDIO_BITS_PER_SAMPLE, TargetBitsPerSample);
        outType.SetUINT32(MfAttr.MF_MT_AUDIO_SAMPLES_PER_SECOND, TargetSampleRate);
        outType.SetUINT32(MfAttr.MF_MT_AUDIO_NUM_CHANNELS, TargetChannels);
        outType.SetUINT32(MfAttr.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(_targetBitRate / 8));
        outType.SetUINT32(MfAttr.MF_MT_AUDIO_BLOCK_ALIGNMENT, 1);
        outType.SetUINT32(MfAttr.MF_MT_AAC_PAYLOAD_TYPE, 1); // 1 = ADTS framing
        _transform!.SetOutputType(0, outType, 0);

        PInvoke.MFCreateMediaType(out var inType).ThrowOnFailure();
        inType.SetGUID(MfAttr.MF_MT_MAJOR_TYPE, MfAttr.MFMediaType_Audio);
        inType.SetGUID(MfAttr.MF_MT_SUBTYPE, MfAttr.MFAudioFormat_PCM);
        inType.SetUINT32(MfAttr.MF_MT_AUDIO_BITS_PER_SAMPLE, TargetBitsPerSample);
        inType.SetUINT32(MfAttr.MF_MT_AUDIO_SAMPLES_PER_SECOND, TargetSampleRate);
        inType.SetUINT32(MfAttr.MF_MT_AUDIO_NUM_CHANNELS, TargetChannels);
        inType.SetUINT32(MfAttr.MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)(TargetChannels * TargetBitsPerSample / 8));
        inType.SetUINT32(MfAttr.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(TargetSampleRate * TargetChannels * TargetBitsPerSample / 8));
        _transform.SetInputType(0, inType, 0);
    }

    /// <summary>
    /// Feeds PCM samples into the encoder. Accumulated ADTS frames are
    /// stored internally; call <see cref="FlushChunk"/> to retrieve them.
    /// </summary>
    public void Encode(PcmFrameI16 pcmFrame)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MfAacEncoder));

        var src = pcmFrame.Samples;
        int srcOffset = 0;
        int srcSamples = src.Length / TargetChannels;

        while (srcSamples > 0)
        {
            int needed = AacFrameSize - _accumulatedSamples;
            int take = Math.Min(needed, srcSamples);
            int accBase = _accumulatedSamples * TargetChannels;
            Array.Copy(src, srcOffset, _accumulator, accBase, take * TargetChannels);

            _accumulatedSamples += take;
            srcOffset += take * TargetChannels;
            srcSamples -= take;

            if (_accumulatedSamples < AacFrameSize) break;

            EncodeFullFrame();
            _accumulatedSamples = 0;
        }
    }

    /// <summary>
    /// Returns the accumulated ADTS bytes since the last flush, or
    /// <see cref="ReadOnlyMemory{T}.Empty"/> if nothing is pending.
    /// </summary>
    public ReadOnlyMemory<byte> FlushChunk()
    {
        if (_batchOffset == 0) return ReadOnlyMemory<byte>.Empty;
        var chunk = new ReadOnlyMemory<byte>(_batchBuffer, 0, _batchOffset);
        _batchBuffer = new byte[16384];
        _batchOffset = 0;
        return chunk;
    }

    private void EncodeFullFrame()
    {
        int byteCount = AacFrameSize * TargetChannels * 2;
        PInvoke.MFCreateMemoryBuffer((uint)byteCount, out var buffer).ThrowOnFailure();

        byte* pData;
        uint maxLen;
        uint currentLen;
        buffer.Lock(&pData, &maxLen, &currentLen);
        try
        {
            fixed (short* src = _accumulator)
            {
                Buffer.MemoryCopy(src, pData, byteCount, byteCount);
            }
        }
        finally { buffer.Unlock(); }
        buffer.SetCurrentLength((uint)byteCount);

        PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
        sample.AddBuffer(buffer);

        // Timestamps are mandatory on every input sample (100-ns units).
        long durationHns = (long)AacFrameSize * 10_000_000L / TargetSampleRate;
        long pts = _sampleCount * 10_000_000L / TargetSampleRate;
        sample.SetSampleTime(pts);
        sample.SetSampleDuration(durationHns);
        _sampleCount += AacFrameSize;

        _transform!.ProcessInput(0, sample, 0);
        DrainOutput();
    }

    private void DrainOutput()
    {
        while (true)
        {
            _transform!.GetOutputStreamInfo(0, out var streamInfo);
            uint outputSize = streamInfo.cbSize == 0 ? 8192 : streamInfo.cbSize;

            PInvoke.MFCreateMemoryBuffer(outputSize, out var outBuffer).ThrowOnFailure();
            PInvoke.MFCreateSample(out var outSample).ThrowOnFailure();
            outSample.AddBuffer(outBuffer);

            var dataBufs = new MFT_OUTPUT_DATA_BUFFER[1];
            dataBufs[0].dwStreamID = 0;
            dataBufs[0].pSample = outSample;
            dataBufs[0].dwStatus = 0;
            dataBufs[0].pEvents = null;

            try
            {
                _transform!.ProcessOutput(0, 1, dataBufs, out _);
            }
            catch (COMException ex)
            {
                if (ex.HResult == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                if (ex.HResult == MF_E_TRANSFORM_STREAM_CHANGE) continue;
                throw;
            }

            byte* pData;
            uint max;
            uint curLen;
            outBuffer.Lock(&pData, &max, &curLen);
            try
            {
                AppendToBatch(pData, (int)curLen);
            }
            finally { outBuffer.Unlock(); }
        }
    }

    private unsafe void AppendToBatch(byte* src, int len)
    {
        if (_batchOffset + len > _batchBuffer.Length)
        {
            int newSize = Math.Max(_batchBuffer.Length * 2, _batchOffset + len);
            Array.Resize(ref _batchBuffer, newSize);
        }
        Marshal.Copy((IntPtr)src, _batchBuffer, _batchOffset, len);
        _batchOffset += len;
    }

    /// <summary>
    /// Signals end-of-stream and returns any final accumulated bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Drain()
    {
        if (_disposed) return ReadOnlyMemory<byte>.Empty;
        try
        {
            _transform?.ProcessMessage((MFT_MESSAGE_TYPE)MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
            DrainOutput();
        }
        catch { }
        return FlushChunk();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _transform?.ProcessMessage((MFT_MESSAGE_TYPE)MFT_MESSAGE_NOTIFY_END_STREAMING, 0); } catch { }
        if (_transform != null) { Marshal.ReleaseComObject(_transform); _transform = null; }
        if (_mfStarted)
        {
            try { PInvoke.MFShutdown(); } catch { }
            _mfStarted = false;
        }
    }
}

// CsWin32 exposes these MF attribute GUIDs as static readonly fields on
// Windows.Win32.Media.MediaFoundation.PInvoke; pull them out here so the
// encoder stays readable.
internal static class MfAttr
{
    public static readonly Guid MF_MT_MAJOR_TYPE = PInvoke.MF_MT_MAJOR_TYPE;
    public static readonly Guid MF_MT_SUBTYPE = PInvoke.MF_MT_SUBTYPE;
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE;
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND;
    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = PInvoke.MF_MT_AUDIO_NUM_CHANNELS;
    public static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT;
    public static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND;
    public static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = PInvoke.MF_MT_AAC_PAYLOAD_TYPE;
    public static readonly Guid MFAudioFormat_PCM = PInvoke.MFAudioFormat_PCM;
    public static readonly Guid MFAudioFormat_AAC = PInvoke.MFAudioFormat_AAC;
    public static readonly Guid MFAudioFormat_Float = PInvoke.MFAudioFormat_Float;
    public static readonly Guid MFMediaType_Audio = PInvoke.MFMediaType_Audio;
}
