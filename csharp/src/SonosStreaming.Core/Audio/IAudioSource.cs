using SonosStreaming.Core.Audio;

namespace SonosStreaming.Core.Audio;

public interface IAudioSource : IDisposable
{
    Task<PcmFrameF32?> NextFrameAsync(CancellationToken ct);
    MixFormat MixFormat { get; }
    void Shutdown();

    /// <summary>Capture buffers the source discarded because the reader lagged.</summary>
    long DroppedBuffers => 0;

    /// <summary>Sample frames (per channel) lost to those discarded buffers.</summary>
    long DroppedSampleFrames => 0;
}

public sealed record MixFormat(uint SampleRate, ushort Channels, ushort BitsPerSample, bool IsFloat);
