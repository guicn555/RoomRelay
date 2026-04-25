using SonosStreaming.Core.State;

namespace SonosStreaming.Core.Audio.Dsp;

public sealed class VuMeter
{
    private readonly object _lock = new();
    private float _rmsL;
    private float _rmsR;
    private float _peakL;
    private float _peakR;
    private readonly float _decay;
    private readonly int _windowSamples;
    private readonly Queue<float> _windowL = new();
    private readonly Queue<float> _windowR = new();

    public float RmsL => _rmsL;
    public float RmsR => _rmsR;
    public float PeakL => _peakL;
    public float PeakR => _peakR;
    public float DbL => ToDb(_rmsL);
    public float DbR => ToDb(_rmsR);

    public VuMeter(float decay = 0.95f, int windowMs = 50, int sampleRate = 48000)
    {
        _decay = decay;
        _windowSamples = sampleRate * windowMs / 1000;
    }

    public void Process(ReadOnlySpan<float> samples, ushort channels)
    {
        if (channels != 2) return;

        float sumL = 0, sumR = 0;
        float peakL = 0, peakR = 0;
        int frames = samples.Length / 2;

        for (int i = 0; i < samples.Length; i += 2)
        {
            float l = samples[i];
            float r = samples[i + 1];
            float l2 = l * l;
            float r2 = r * r;
            sumL += l2;
            sumR += r2;
            peakL = Math.Max(peakL, MathF.Abs(l));
            peakR = Math.Max(peakR, MathF.Abs(r));
        }

        lock (_lock)
        {
            float newRmsL = MathF.Sqrt(sumL / frames);
            float newRmsR = MathF.Sqrt(sumR / frames);
            _rmsL = Math.Max(newRmsL, _rmsL * _decay);
            _rmsR = Math.Max(newRmsR, _rmsR * _decay);
            _peakL = Math.Max(peakL, _peakL * _decay);
            _peakR = Math.Max(peakR, _peakR * _decay);
        }
    }

    private static float ToDb(float linear) => linear > 0 ? 20f * MathF.Log10(linear) : -60f;
}
