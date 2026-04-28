namespace SonosStreaming.Core.Audio;

// Raw 16-bit network-order PCM batcher for Sonos low-latency streaming.
// audio/L16 uses big-endian samples and carries rate/channel information in
// the Content-Type header, so no WAV container header is emitted.
public sealed class L16PcmEncoder : IAudioEncoder
{
    private byte[] _batchBuffer = new byte[32768];
    private int _batchOffset;
    private bool _disposed;

    public void Encode(PcmFrameI16 pcmFrame)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(L16PcmEncoder));
        var samples = pcmFrame.Samples;
        int byteCount = samples.Length * 2;
        EnsureCapacity(byteCount);

        for (int i = 0; i < samples.Length; i++)
        {
            ushort sample = unchecked((ushort)samples[i]);
            _batchBuffer[_batchOffset++] = (byte)(sample >> 8);
            _batchBuffer[_batchOffset++] = (byte)(sample & 0xFF);
        }
    }

    // ~43 ms of 48 kHz stereo 16-bit PCM. Keep this lower than WAV's previous
    // 85 ms cadence to reduce latency for video use.
    private const int MinFlushBytes = 8192;

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
        _batchBuffer = new byte[32768];
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
