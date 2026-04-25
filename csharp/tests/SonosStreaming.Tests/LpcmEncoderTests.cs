using SonosStreaming.Core.Audio;
using FluentAssertions;
using Xunit;

namespace SonosStreaming.Tests;

public class LpcmEncoderTests
{
    private static PcmFrameI16 MakeFrame(short[] samples, ushort channels = 2, uint sampleRate = 48000)
        => new(samples, sampleRate, channels);

    [Fact]
    public void Encode_SmallFrame_DoesNotFlush()
    {
        using var enc = new LpcmEncoder();
        var frame = MakeFrame(new short[480]);
        enc.Encode(frame);

        enc.FlushChunk().Should().Be(ReadOnlyMemory<byte>.Empty);
    }

    [Fact]
    public void Encode_MultipleFrames_FlushesWhenThresholdReached()
    {
        using var enc = new LpcmEncoder();
        for (int i = 0; i < 20; i++)
        {
            var frame = MakeFrame(new short[480]);
            enc.Encode(frame);
        }

        var chunk = enc.FlushChunk();
        chunk.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Drain_FlushesAllRemainingData()
    {
        using var enc = new LpcmEncoder();
        var frame = MakeFrame(new short[480]);
        enc.Encode(frame);

        enc.FlushChunk().Should().Be(ReadOnlyMemory<byte>.Empty);
        var drained = enc.Drain();
        drained.Length.Should().Be(480 * 2);
    }

    [Fact]
    public void Drain_ReturnsEmptyAfterDrain()
    {
        using var enc = new LpcmEncoder();
        enc.Encode(MakeFrame(new short[480]));
        enc.Drain();
        enc.Drain().Length.Should().Be(0);
    }

    [Fact]
    public void Encode_ProducesCorrectByteArray()
    {
        using var enc = new LpcmEncoder();
        var samples = new short[] { 0x0100, 0x0200 };
        enc.Encode(MakeFrame(samples));
        var drained = enc.Drain();

        drained.Span[0].Should().Be(0x00);
        drained.Span[1].Should().Be(0x01);
        drained.Span[2].Should().Be(0x00);
        drained.Span[3].Should().Be(0x02);
    }

    [Fact]
    public void Dispose_ThenEncode_ThrowsObjectDisposedException()
    {
        var enc = new LpcmEncoder();
        enc.Dispose();
        var act = () => enc.Encode(MakeFrame(new short[100]));
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_ThenDrain_ReturnsEmpty()
    {
        var enc = new LpcmEncoder();
        enc.Encode(MakeFrame(new short[480]));
        enc.Dispose();
        enc.Drain().Should().Be(ReadOnlyMemory<byte>.Empty);
    }

    [Fact]
    public void FlushChunk_AfterFullFlush_ReturnsEmpty()
    {
        using var enc = new LpcmEncoder();
        for (int i = 0; i < 20; i++)
            enc.Encode(MakeFrame(new short[480]));

        var first = enc.FlushChunk();
        first.Length.Should().BeGreaterThan(0);
        enc.FlushChunk().Should().Be(ReadOnlyMemory<byte>.Empty);
    }

    [Fact]
    public void Encode_LargeFrame_ExpandsBuffer()
    {
        using var enc = new LpcmEncoder();
        var bigFrame = MakeFrame(new short[65536]);
        enc.Encode(bigFrame);
        var drained = enc.Drain();
        drained.Length.Should().Be(65536 * 2);
    }
}