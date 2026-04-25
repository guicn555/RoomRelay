using SonosStreaming.Core.Audio;

namespace SonosStreaming.Core.Audio;

public interface IAudioSource : IDisposable
{
    Task<PcmFrameF32?> NextFrameAsync(CancellationToken ct);
    MixFormat MixFormat { get; }
    void Shutdown();
}

public sealed record MixFormat(uint SampleRate, ushort Channels, ushort BitsPerSample, bool IsFloat);
