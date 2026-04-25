namespace SonosStreaming.Core.Audio;

public interface IAudioEncoder : IDisposable
{
    void Encode(PcmFrameI16 pcmFrame);
    ReadOnlyMemory<byte> FlushChunk();
    ReadOnlyMemory<byte> Drain();
}
