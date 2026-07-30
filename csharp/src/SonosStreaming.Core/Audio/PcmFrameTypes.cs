namespace SonosStreaming.Core.Audio;

public sealed class PcmFrameF32
{
    public float[] Samples { get; init; }
    public uint SampleRate { get; init; }
    public ushort Channels { get; init; }

    public PcmFrameF32(float[] samples, uint sampleRate, ushort channels)
    {
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public int FrameCount => Samples.Length / Channels;

    public static PcmFrameF32 Silent(int frames, uint sampleRate, ushort channels)
    {
        return new PcmFrameF32(new float[frames * channels], sampleRate, channels);
    }
}

public sealed class PcmFrameI16
{
    public short[] Samples { get; init; }
    public uint SampleRate { get; init; }
    public ushort Channels { get; init; }

    public PcmFrameI16(short[] samples, uint sampleRate, ushort channels)
    {
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public int FrameCount => Samples.Length / Channels;
}

public static class PcmConvert
{
    /// <summary>
    /// Folds a multi-channel frame down to stereo. Returns the frame unchanged
    /// if it is already stereo. Mono is duplicated to L+R. For N > 2 channels,
    /// even-indexed channels fold to L and odd-indexed channels fold to R,
    /// normalised so peak amplitude is preserved.
    /// </summary>
    public static PcmFrameF32 DownmixToStereo(PcmFrameF32 frame)
    {
        if (frame.Channels == 2) return frame;

        int frameCount = frame.FrameCount;
        int inCh = frame.Channels;
        var output = new float[frameCount * 2];

        if (inCh == 1)
        {
            for (int f = 0; f < frameCount; f++)
            {
                output[f * 2]     = frame.Samples[f];
                output[f * 2 + 1] = frame.Samples[f];
            }
            return new PcmFrameF32(output, frame.SampleRate, 2);
        }

        // WAVEFORMATEXTENSIBLE standard layouts (KSAUDIO_SPEAKER_5POINT1 / 7POINT1):
        //   5.1: FL FR FC LFE BL BR
        //   7.1: FL FR FC LFE BL BR SL SR
        // ITU-R BS.775 style: FC and LFE split equally to L+R, surrounds at -3 dB.
        const float c3dB = 0.7071f;
        for (int f = 0; f < frameCount; f++)
        {
            int b = f * inCh;
            float l, r;
            switch (inCh)
            {
                case 6: // 5.1
                    l = frame.Samples[b]   + c3dB * frame.Samples[b+2] + c3dB * frame.Samples[b+4] + frame.Samples[b+3];
                    r = frame.Samples[b+1] + c3dB * frame.Samples[b+2] + c3dB * frame.Samples[b+5] + frame.Samples[b+3];
                    break;
                case 8: // 7.1
                    l = frame.Samples[b]   + c3dB * frame.Samples[b+2] + c3dB * frame.Samples[b+4] + c3dB * frame.Samples[b+6] + frame.Samples[b+3];
                    r = frame.Samples[b+1] + c3dB * frame.Samples[b+2] + c3dB * frame.Samples[b+5] + c3dB * frame.Samples[b+7] + frame.Samples[b+3];
                    break;
                default: // generic even/odd fallback for unusual layouts
                    l = 0f; r = 0f;
                    for (int ch = 0; ch < inCh; ch++)
                    {
                        if ((ch & 1) == 0) l += frame.Samples[b + ch];
                        else               r += frame.Samples[b + ch];
                    }
                    l /= (inCh + 1) / 2;
                    r /= inCh / 2;
                    break;
            }
            output[f * 2]     = l;
            output[f * 2 + 1] = r;
        }
        return new PcmFrameF32(output, frame.SampleRate, 2);
    }

    public static void F32ToI16(ReadOnlySpan<float> input, List<short> output)
    {
        output.Clear();
        output.EnsureCapacity(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            float clamped = Math.Clamp(input[i], -1f, 1f);
            output.Add((short)Math.Round(clamped * short.MaxValue));
        }
    }

    public static void F32ToI16(ReadOnlySpan<float> input, Span<short> output)
    {
        for (int i = 0; i < input.Length; i++)
        {
            float clamped = Math.Clamp(input[i], -1f, 1f);
            output[i] = (short)Math.Round(clamped * short.MaxValue);
        }
    }
}
