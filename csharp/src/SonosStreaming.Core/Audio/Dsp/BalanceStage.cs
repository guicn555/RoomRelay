namespace SonosStreaming.Core.Audio.Dsp;

public sealed class BalanceStage
{
    private volatile float _balance;

    public float Balance
    {
        get => _balance;
        set => _balance = Math.Clamp(value, -1f, 1f);
    }

    public void Apply(Span<float> samples, ushort channels)
    {
        if (channels < 2 || samples.Length == 0) return;

        var balance = _balance;
        if (Math.Abs(balance) < 0.0001f) return;

        var leftGain = balance > 0f ? 1f - balance : 1f;
        var rightGain = balance < 0f ? 1f + balance : 1f;
        var ch = Math.Max(1, (int)channels);

        for (int i = 0; i + 1 < samples.Length; i += ch)
        {
            samples[i] *= leftGain;
            samples[i + 1] *= rightGain;
        }
    }
}
