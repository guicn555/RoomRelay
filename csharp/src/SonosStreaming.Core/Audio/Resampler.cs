using System.Runtime.InteropServices;
using Serilog;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace SonosStreaming.Core.Audio;

// Converts captured PcmFrameF32 (whatever the device mix rate is) into
// PcmFrameI16 at 48 kHz stereo for the AAC encoder.
//
// Fast path: if input is already 48 kHz stereo, just scale f32 -> i16
// inline (no external dependency). Slow path: hand the samples to the
// Windows Media Foundation resampler MFT (CLSID_CResamplerMediaObject),
// which is built in to Windows.
public sealed unsafe class Resampler : IDisposable
{
    public const uint TargetRate = 48000;
    public const ushort TargetChannels = 2;

    private const uint MFSTARTUP_FULL = 0;
    private const uint MF_VERSION = 0x00020070;
    private const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    private const uint MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001;
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    private const uint CLSCTX_INPROC_SERVER = 0x1;

    private static readonly Guid CLSID_CResamplerMediaObject = new("f447b69e-1884-4a7e-8055-346f74d6edb3");

    [DllImport("Ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    private readonly uint _inputRate;
    private readonly ushort _channels;
    private readonly bool _needsResample;
    private IMFTransform? _resampler;
    private bool _mfStarted;
    private long _inputTimeHns;
    private bool _disposed;

    public Resampler(uint inputRate, ushort channels)
    {
        if (channels != 2)
            throw new ArgumentException($"Expected stereo input, got {channels} channels", nameof(channels));

        _inputRate = inputRate;
        _channels = channels;
        _needsResample = inputRate != TargetRate;

        if (_needsResample)
        {
            InitMfResampler();
            Log.Information("Resampler: {InRate} Hz f32 -> {OutRate} Hz i16 (MF CResampler)", inputRate, TargetRate);
        }
        else
        {
            Log.Information("Resampler: {InRate} Hz f32 -> i16 pass-through", inputRate);
        }
    }

    private void InitMfResampler()
    {
        PInvoke.MFStartup(MF_VERSION, MFSTARTUP_FULL).ThrowOnFailure();
        _mfStarted = true;

        var iidIMFTransform = typeof(IMFTransform).GUID;
        int hr = CoCreateInstance(CLSID_CResamplerMediaObject, IntPtr.Zero, CLSCTX_INPROC_SERVER, iidIMFTransform, out var p);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        _resampler = (IMFTransform)Marshal.GetObjectForIUnknown(p);
        Marshal.Release(p);

        // Input: float32 interleaved at the device mix rate.
        PInvoke.MFCreateMediaType(out var inType).ThrowOnFailure();
        inType.SetGUID(MfAttr.MF_MT_MAJOR_TYPE, MfAttr.MFMediaType_Audio);
        inType.SetGUID(MfAttr.MF_MT_SUBTYPE, MfAttr.MFAudioFormat_Float);
        inType.SetUINT32(MfAttr.MF_MT_AUDIO_BITS_PER_SAMPLE, 32);
        inType.SetUINT32(MfAttr.MF_MT_AUDIO_SAMPLES_PER_SECOND, _inputRate);
        inType.SetUINT32(MfAttr.MF_MT_AUDIO_NUM_CHANNELS, _channels);
        inType.SetUINT32(MfAttr.MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)(_channels * 4));
        inType.SetUINT32(MfAttr.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, _inputRate * (uint)_channels * 4u);
        _resampler!.SetInputType(0, inType, 0);

        // Output: int16 interleaved at 48 kHz.
        PInvoke.MFCreateMediaType(out var outType).ThrowOnFailure();
        outType.SetGUID(MfAttr.MF_MT_MAJOR_TYPE, MfAttr.MFMediaType_Audio);
        outType.SetGUID(MfAttr.MF_MT_SUBTYPE, MfAttr.MFAudioFormat_PCM);
        outType.SetUINT32(MfAttr.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        outType.SetUINT32(MfAttr.MF_MT_AUDIO_SAMPLES_PER_SECOND, TargetRate);
        outType.SetUINT32(MfAttr.MF_MT_AUDIO_NUM_CHANNELS, TargetChannels);
        outType.SetUINT32(MfAttr.MF_MT_AUDIO_BLOCK_ALIGNMENT, TargetChannels * 2u);
        outType.SetUINT32(MfAttr.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, TargetRate * TargetChannels * 2u);
        _resampler.SetOutputType(0, outType, 0);

        _resampler.ProcessMessage((MFT_MESSAGE_TYPE)MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    }

    public PcmFrameI16 Process(PcmFrameF32 frame)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(Resampler));

        if (!_needsResample)
        {
            var output = new short[frame.Samples.Length];
            PcmConvert.F32ToI16(frame.Samples, output);
            return new PcmFrameI16(output, TargetRate, _channels);
        }

        return ResampleViaMf(frame);
    }

    private PcmFrameI16 ResampleViaMf(PcmFrameF32 frame)
    {
        // Wrap the float32 input in an IMFSample and push it through the
        // MFT. One input buffer can produce one or more output buffers,
        // which we concatenate into a single PcmFrameI16.
        int inBytes = frame.Samples.Length * 4;
        PInvoke.MFCreateMemoryBuffer((uint)inBytes, out var inBuf).ThrowOnFailure();

        byte* pIn;
        uint maxLen;
        uint curLen;
        inBuf.Lock(&pIn, &maxLen, &curLen);
        try
        {
            fixed (float* src = frame.Samples)
            {
                Buffer.MemoryCopy(src, pIn, inBytes, inBytes);
            }
        }
        finally { inBuf.Unlock(); }
        inBuf.SetCurrentLength((uint)inBytes);

        PInvoke.MFCreateSample(out var inSample).ThrowOnFailure();
        inSample.AddBuffer(inBuf);

        long durationHns = (long)frame.Samples.Length / _channels * 10_000_000L / _inputRate;
        inSample.SetSampleTime(_inputTimeHns);
        inSample.SetSampleDuration(durationHns);
        _inputTimeHns += durationHns;

        _resampler!.ProcessInput(0, inSample, 0);

        // Drain every output packet we can produce from this one input.
        var collected = new List<short[]>();
        while (true)
        {
            _resampler.GetOutputStreamInfo(0, out var info);
            uint outSize = info.cbSize == 0 ? 8192 : info.cbSize;
            PInvoke.MFCreateMemoryBuffer(outSize, out var outBuf).ThrowOnFailure();
            PInvoke.MFCreateSample(out var outSample).ThrowOnFailure();
            outSample.AddBuffer(outBuf);

            var bufs = new MFT_OUTPUT_DATA_BUFFER[1];
            bufs[0].dwStreamID = 0;
            bufs[0].pSample = outSample;
            bufs[0].dwStatus = 0;
            bufs[0].pEvents = null;

            try
            {
                _resampler.ProcessOutput(0, 1, bufs, out _);
            }
            catch (COMException ex)
            {
                if (ex.HResult == MF_E_TRANSFORM_NEED_MORE_INPUT) break;
                if (ex.HResult == MF_E_TRANSFORM_STREAM_CHANGE) continue;
                throw;
            }

            byte* pOut;
            uint outMax;
            uint outCur;
            outBuf.Lock(&pOut, &outMax, &outCur);
            try
            {
                int samples = (int)outCur / 2;
                var chunk = new short[samples];
                fixed (short* dst = chunk)
                {
                    Buffer.MemoryCopy(pOut, dst, outCur, outCur);
                }
                collected.Add(chunk);
            }
            finally { outBuf.Unlock(); }
        }

        int total = 0;
        for (int i = 0; i < collected.Count; i++) total += collected[i].Length;
        var output = new short[total];
        int off = 0;
        for (int i = 0; i < collected.Count; i++)
        {
            var c = collected[i];
            Array.Copy(c, 0, output, off, c.Length);
            off += c.Length;
        }
        return new PcmFrameI16(output, TargetRate, TargetChannels);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _resampler?.ProcessMessage((MFT_MESSAGE_TYPE)MFT_MESSAGE_NOTIFY_END_STREAMING, 0); } catch { }
        if (_resampler != null) { Marshal.ReleaseComObject(_resampler); _resampler = null; }
        if (_mfStarted)
        {
            try { PInvoke.MFShutdown(); } catch { }
            _mfStarted = false;
        }
    }
}
