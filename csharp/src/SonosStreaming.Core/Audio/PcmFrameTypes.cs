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
