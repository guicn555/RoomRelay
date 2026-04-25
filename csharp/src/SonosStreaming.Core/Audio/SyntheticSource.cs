using System.Threading.Channels;
using Serilog;

namespace SonosStreaming.Core.Audio;

public enum SyntheticPattern
{
    Sine,
    Silence,
    WhiteNoise,
}

public sealed class SyntheticSource : IAudioSource
{
    private readonly SyntheticPattern _pattern;
    private readonly float _frequency;
    private readonly float _amplitude;
    private uint _sampleRate;
    private ushort _channels;
    private int _framesPerCall;
    private ulong _tSamples;
    private uint _rngState;
    private bool _realtime;
    private ulong? _remainingFrames;
    private DateTime? _nextDeadline;
    private bool _disposed;

    public SyntheticSource(SyntheticPattern pattern, float frequency = 1000f, float amplitude = 0.5f)
    {
        _pattern = pattern;
        _frequency = frequency;
        _amplitude = amplitude;
        _sampleRate = 48000;
        _channels = 2;
        _framesPerCall = 1024;
        _rngState = 0x1234_5678;
        _realtime = false;
    }

    public static SyntheticSource Sine(float frequency = 1000f, float amplitude = 0.5f)
        => new(SyntheticPattern.Sine, frequency, amplitude);

    public static SyntheticSource Silence() => new(SyntheticPattern.Silence);

    public static SyntheticSource WhiteNoise(float amplitude = 0.5f)
        => new(SyntheticPattern.WhiteNoise, amplitude: amplitude);

    public SyntheticSource WithSampleRate(uint rate) { _sampleRate = rate; return this; }
    public SyntheticSource WithChannels(ushort ch) { _channels = ch; return this; }
    public SyntheticSource WithFramesPerCall(int frames) => new(_pattern, _frequency, _amplitude) { _framesPerCall = frames, _sampleRate = _sampleRate, _channels = _channels };
    public SyntheticSource WithRealtime(bool realtime) => new(_pattern, _frequency, _amplitude) { _realtime = realtime, _sampleRate = _sampleRate, _channels = _channels };
    public SyntheticSource WithDurationFrames(ulong frames) { _remainingFrames = frames; return this; }

    public MixFormat MixFormat => new(_sampleRate, _channels, 32, true);

    public Task<PcmFrameF32?> NextFrameAsync(CancellationToken ct)
    {
        if (_disposed) return Task.FromResult<PcmFrameF32?>(null);

        int framesNow = _remainingFrames switch
        {
            null => _framesPerCall,
            0 => Task.FromResult<PcmFrameF32?>(null).Result is null ? 0 : 0,
            ulong r => (int)Math.Min((ulong)_framesPerCall, r),
        };

        if (_remainingFrames == 0 || framesNow == 0)
            return Task.FromResult<PcmFrameF32?>(null);

        var samples = new float[framesNow * _channels];
        for (int f = 0; f < framesNow; f++)
        {
            for (int ch = 0; ch < _channels; ch++)
            {
                samples[f * _channels + ch] = GenSample();
            }
            _tSamples++;
        }

        if (_remainingFrames.HasValue)
            _remainingFrames = Math.Max(0, _remainingFrames.Value - (ulong)framesNow);

        if (_realtime)
        {
            var chunkDur = TimeSpan.FromMicroseconds((long)framesNow * 1_000_000 / _sampleRate);
            _nextDeadline = (_nextDeadline ?? DateTime.UtcNow) + chunkDur;
            var now = DateTime.UtcNow;
            if (_nextDeadline > now)
                Thread.Sleep(_nextDeadline.Value - now);
            else
                _nextDeadline = now + chunkDur;
        }

        return Task.FromResult<PcmFrameF32?>(new PcmFrameF32(samples, _sampleRate, _channels));
    }

    private float GenSample()
    {
        return _pattern switch
        {
            SyntheticPattern.Silence => 0f,
            SyntheticPattern.Sine => MathF.Sin(2f * MathF.PI * _frequency * _tSamples / _sampleRate) * _amplitude,
            SyntheticPattern.WhiteNoise => XorShift32() * _amplitude,
            _ => 0f,
        };
    }

    private float XorShift32()
    {
        uint x = _rngState;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _rngState = x;
        return (x / (float)uint.MaxValue) * 2f - 1f;
    }

    public void Shutdown()
    {
        _disposed = true;
        _remainingFrames = 0;
    }

    public void Dispose()
    {
        Shutdown();
    }
}
