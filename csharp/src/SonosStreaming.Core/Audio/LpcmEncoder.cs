namespace SonosStreaming.Core.Audio;

// Raw little-endian 16-bit interleaved PCM batcher for Sonos lossless streaming.
// The WAV container header is injected per-connection by StreamServer so every
// late-joining client gets a valid RIFF/WAVE header (data chunk size = 0xFFFFFFFF).
public sealed class LpcmEncoder : IAudioEncoder
{
    private byte[] _batchBuffer = new byte[65536];
    private int _batchOffset;
    private bool _disposed;

    public LpcmEncoder()
    {
    }

    public void Encode(PcmFrameI16 pcmFrame)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LpcmEncoder));
        var samples = pcmFrame.Samples;
        int byteCount = samples.Length * 2;
        EnsureCapacity(byteCount);
        Buffer.BlockCopy(samples, 0, _batchBuffer, _batchOffset, byteCount);
        _batchOffset += byteCount;
    }

    // ~85 ms of 48 kHz stereo 16-bit PCM — matches AAC chunk cadence to avoid
    // excessive TCP writes and GC pressure from allocating buffers every 20 ms.
    private const int MinFlushBytes = 16384;

    public ReadOnlyMemory<byte> FlushChunk()
    {
        if (_batchOffset < MinFlushBytes) return ReadOnlyMemory<byte>.Empty;
        return DoFlush();
    }

    public ReadOnlyMemory<byte> Drain()
    {
        if (_disposed) return ReadOnlyMemory<byte>.Empty;
        return DoFlush();
    }

    private ReadOnlyMemory<byte> DoFlush()
    {
        if (_batchOffset == 0) return ReadOnlyMemory<byte>.Empty;
        var chunk = new ReadOnlyMemory<byte>(_batchBuffer, 0, _batchOffset);
        _batchBuffer = new byte[65536];
        _batchOffset = 0;
        return chunk;
    }

    public void Dispose() => _disposed = true;

    private void EnsureCapacity(int needed)
    {
        if (_batchOffset + needed > _batchBuffer.Length)
            Array.Resize(ref _batchBuffer, Math.Max(_batchBuffer.Length * 2, _batchOffset + needed));
    }
}
