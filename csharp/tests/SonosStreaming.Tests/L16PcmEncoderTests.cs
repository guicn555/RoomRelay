using FluentAssertions;
using SonosStreaming.Core.Audio;
using Xunit;

namespace SonosStreaming.Tests;

public sealed class L16PcmEncoderTests
{
    private static PcmFrameI16 MakeFrame(short[] samples, ushort channels = 2, uint sampleRate = 48000)
        => new(samples, sampleRate, channels);

    [Fact]
    public void Encode_ProducesBigEndianBytes()
    {
        using var enc = new L16PcmEncoder();
        var samples = new short[] { 0x0102, unchecked((short)0xFE03) };

        enc.Encode(MakeFrame(samples));
        var drained = enc.Drain();

        drained.ToArray().Should().Equal(0x01, 0x02, 0xFE, 0x03);
    }

    [Fact]
    public void Drain_FlushesAllRemainingData()
    {
        using var enc = new L16PcmEncoder();

        enc.Encode(MakeFrame(new short[480]));

        enc.FlushChunk().Should().Be(ReadOnlyMemory<byte>.Empty);
        enc.Drain().Length.Should().Be(480 * 2);
    }

    [Fact]
    public void Dispose_ThenEncode_ThrowsObjectDisposedException()
    {
        var enc = new L16PcmEncoder();
        enc.Dispose();

        var act = () => enc.Encode(MakeFrame(new short[100]));

        act.Should().Throw<ObjectDisposedException>();
    }
}
