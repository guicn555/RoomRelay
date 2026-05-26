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

        // Even-indexed channels (FL, FC, BL, FLC, …) → L
        // Odd-indexed channels  (FR, LFE, BR, FRC, …) → R
        int evenCount = (inCh + 1) / 2;
        int oddCount  = inCh / 2;
        float scaleL  = 1f / evenCount;
        float scaleR  = 1f / oddCount;
        for (int f = 0; f < frameCount; f++)
        {
            float l = 0f, r = 0f;
            int baseIdx = f * inCh;
            for (int c = 0; c < inCh; c++)
            {
                if ((c & 1) == 0) l += frame.Samples[baseIdx + c];
                else              r += frame.Samples[baseIdx + c];
            }
            output[f * 2]     = l * scaleL;
            output[f * 2 + 1] = r * scaleR;
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
