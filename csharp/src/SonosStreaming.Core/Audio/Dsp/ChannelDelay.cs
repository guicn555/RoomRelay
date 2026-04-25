namespace SonosStreaming.Core.Audio.Dsp;

public sealed class ChannelDelay
{
    private readonly float[] _bufferL;
    private readonly float[] _bufferR;
    private int _writePos;
    private int _delaySamplesL;
    private int _delaySamplesR;

    public float DelayMsL
    {
        get => _delaySamplesL * 1000f / 48000f;
        set => _delaySamplesL = Math.Clamp((int)(value * 48000f / 1000f), 0, _bufferL.Length);
    }

    public float DelayMsR
    {
        get => _delaySamplesR * 1000f / 48000f;
        set => _delaySamplesR = Math.Clamp((int)(value * 48000f / 1000f), 0, _bufferR.Length);
    }

    public ChannelDelay(float maxDelayMs = 100f)
    {
        int maxSamples = (int)(maxDelayMs * 48000f / 1000f) + 1;
        _bufferL = new float[maxSamples];
        _bufferR = new float[maxSamples];
        _delaySamplesL = 0;
        _delaySamplesR = 0;
    }

    public void Process(Span<float> samples, ushort channels)
    {
        if (channels != 2) return;
        int len = _bufferL.Length;

        for (int i = 0; i < samples.Length; i += 2)
        {
            int readPosL = (_writePos - _delaySamplesL + len) % len;
            int readPosR = (_writePos - _delaySamplesR + len) % len;

            float outL = _bufferL[readPosL];
            float outR = _bufferR[readPosR];

            _bufferL[_writePos] = samples[i];
            _bufferR[_writePos] = samples[i + 1];

            samples[i] = outL;
            samples[i + 1] = outR;

            _writePos = (_writePos + 1) % len;
        }
    }
}
