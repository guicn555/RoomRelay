using MathNet.Numerics.IntegralTransforms;

namespace SonosStreaming.Core.Audio.Dsp;

public sealed class SpectrumAnalyzer
{
    private readonly int _fftSize = 2048;
    private readonly int _numBands = 64;
    private readonly float[] _hannWindow;
    private readonly float[] _leftAccum;
    private readonly float[] _rightAccum;
    private readonly float[] _magnitudes;
    private readonly float[] _bandLevels;
    private int _accumCount;

    public ReadOnlySpan<float> BandLevels => _bandLevels;

    public SpectrumAnalyzer()
    {
        _hannWindow = new float[_fftSize];
        for (int i = 0; i < _fftSize; i++)
            _hannWindow[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / _fftSize));
        _leftAccum = new float[_fftSize];
        _rightAccum = new float[_fftSize];
        _magnitudes = new float[_fftSize / 2];
        _bandLevels = new float[_numBands];
    }

    public void Process(ReadOnlySpan<float> samples, ushort channels)
    {
        if (channels != 2) return;
        int frames = samples.Length / 2;

        for (int i = 0; i < frames && _accumCount + i < _fftSize; i++)
        {
            _leftAccum[_accumCount + i] = samples[i * 2];
            _rightAccum[_accumCount + i] = samples[i * 2 + 1];
        }
        _accumCount += frames;

        if (_accumCount < _fftSize) return;
        _accumCount = 0;

        var data = new MathNet.Numerics.Complex32[_fftSize];
        for (int i = 0; i < _fftSize; i++)
            data[i] = new MathNet.Numerics.Complex32((_leftAccum[i] + _rightAccum[i]) * _hannWindow[i] * 0.5f, 0);

        Fourier.Forward(data, FourierOptions.Matlab);

        for (int i = 0; i < _magnitudes.Length; i++)
        {
            float mag = data[i].Magnitude;
            _magnitudes[i] = mag > 0 ? 20f * MathF.Log10(mag) : FloorDb;
        }

        ComputeLogBands();
    }

    private const float FloorDb = -90f;
    private const float CeilDb = 0f;

    private void ComputeLogBands()
    {
        float minFreq = 20f;
        float maxFreq = 20000f;
        float logMin = MathF.Log2(minFreq);
        float logMax = MathF.Log2(maxFreq);
        float binHz = 48000f / _fftSize;
        float dbRange = CeilDb - FloorDb;

        for (int b = 0; b < _numBands; b++)
        {
            float loLog = logMin + (logMax - logMin) * b / _numBands;
            float hiLog = logMin + (logMax - logMin) * (b + 1) / _numBands;
            int loBin = Math.Max(1, (int)(MathF.Pow(2, loLog) / binHz));
            int hiBin = Math.Min(_magnitudes.Length - 1, (int)(MathF.Pow(2, hiLog) / binHz));

            float max = FloorDb;
            for (int i = loBin; i <= hiBin; i++)
                max = Math.Max(max, _magnitudes[i]);

            _bandLevels[b] = Math.Clamp((max - FloorDb) / dbRange, 0f, 1f);
        }
    }
}
