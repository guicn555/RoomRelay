namespace SonosStreaming.Core.Audio.Dsp;

public sealed class VolumeStage
{
    private volatile float _volume = 1.0f;

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 8f);
    }

    public void Apply(Span<float> samples)
    {
        float v = _volume;
        if (MathF.Abs(v - 1f) < 1e-6f) return;
        for (int i = 0; i < samples.Length; i++)
        {
            float s = samples[i] * v;
            samples[i] = s > 1f ? 1f : (s < -1f ? -1f : s);
        }
    }
}
