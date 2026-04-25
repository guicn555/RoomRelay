namespace SonosStreaming.Core.Audio.Dsp;

public sealed class BiquadEqualizer
{
    private const int NumBands = 3;
    private readonly BiquadFilter[,] _filters = new BiquadFilter[2, NumBands];

    private float _lowGainDb;
    private float _midGainDb;
    private float _highGainDb;
    private float _lowFreq = 320f;
    private float _midFreq = 1000f;
    private float _highFreq = 3200f;
    private float _q = 1.0f / MathF.Sqrt(2f);

    public float LowGainDb { get => _lowGainDb; set { _lowGainDb = value; UpdateFilters(); } }
    public float MidGainDb { get => _midGainDb; set { _midGainDb = value; UpdateFilters(); } }
    public float HighGainDb { get => _highGainDb; set { _highGainDb = value; UpdateFilters(); } }
    public float LowFreq { get => _lowFreq; set { _lowFreq = value; UpdateFilters(); } }
    public float MidFreq { get => _midFreq; set { _midFreq = value; UpdateFilters(); } }
    public float HighFreq { get => _highFreq; set { _highFreq = value; UpdateFilters(); } }
    public float Q { get => _q; set { _q = value; UpdateFilters(); } }

    public BiquadEqualizer()
    {
        for (int ch = 0; ch < 2; ch++)
            for (int b = 0; b < NumBands; b++)
                _filters[ch, b] = new BiquadFilter();
        UpdateFilters();
    }

    public void Process(Span<float> samples, ushort channels)
    {
        if (channels != 2) return;
        for (int i = 0; i < samples.Length; i += 2)
        {
            samples[i] = ProcessChannel(0, samples[i]);
            samples[i + 1] = ProcessChannel(1, samples[i + 1]);
        }
    }

    private float ProcessChannel(int ch, float sample)
    {
        float x = sample;
        for (int b = 0; b < NumBands; b++)
            x = _filters[ch, b].Process(x);
        return x;
    }

    private void UpdateFilters()
    {
        SetPeaking(0, _lowFreq, _lowGainDb);
        SetPeaking(1, _midFreq, _midGainDb);
        SetPeaking(2, _highFreq, _highGainDb);
    }

    private void SetPeaking(int band, float freq, float gainDb)
    {
        float a = MathF.Pow(10f, gainDb / 40f);
        float w0 = 2f * MathF.PI * freq / 48000f;
        float cosW0 = MathF.Cos(w0);
        float sinW0 = MathF.Sin(w0);
        float alpha = sinW0 / (2f * _q);

        float b0 = 1f + alpha * a;
        float b1 = -2f * cosW0;
        float b2 = 1f - alpha * a;
        float a0 = 1f + alpha / a;
        float a1 = -2f * cosW0;
        float a2 = 1f - alpha / a;

        for (int ch = 0; ch < 2; ch++)
            _filters[ch, band].SetCoefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    private sealed class BiquadFilter
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        public void SetCoefficients(float b0, float b1, float b2, float a1, float a2)
        {
            _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
        }

        public float Process(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }
    }
}
