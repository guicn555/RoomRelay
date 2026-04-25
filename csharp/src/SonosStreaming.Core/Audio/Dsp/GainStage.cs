using System.Numerics;
using System.Runtime.CompilerServices;

namespace SonosStreaming.Core.Audio.Dsp;

public sealed class GainStage
{
    private volatile float _gainL = 1.0f;
    private volatile float _gainR = 1.0f;
    private volatile bool _linked = true;

    public float GainL
    {
        get => _gainL;
        set
        {
            _gainL = value;
            if (_linked) _gainR = value;
        }
    }

    public float GainR
    {
        get => _gainR;
        set
        {
            _gainR = value;
            if (_linked) _gainL = value;
        }
    }

    public bool Linked
    {
        get => _linked;
        set => _linked = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Apply(Span<float> samples, ushort channels)
    {
        if (channels == 2)
        {
            float gl = _gainL;
            float gr = _gainR;
            if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
            {
                int vecSize = Vector<float>.Count;
                int i = 0;
                for (; i <= samples.Length - vecSize * 2; i += vecSize * 2)
                {
                    var vl = new Vector<float>(samples[i..(i + vecSize)]);
                    var vr = new Vector<float>(samples[(i + vecSize)..(i + vecSize * 2)]);
                    (vl * gl).CopyTo(samples[i..]);
                    (vr * gr).CopyTo(samples[(i + vecSize)..]);
                }
                for (; i < samples.Length; i += 2)
                {
                    samples[i] *= gl;
                    samples[i + 1] *= gr;
                }
            }
            else
            {
                for (int i = 0; i < samples.Length; i += 2)
                {
                    samples[i] *= gl;
                    samples[i + 1] *= gr;
                }
            }
            return;
        }

        int ch = Math.Max(1, (int)channels);
        float gl2 = _gainL, gr2 = _gainR;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] *= (i % ch == 0) ? gl2 : gr2;
        }
    }
}
