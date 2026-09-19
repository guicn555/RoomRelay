using FluentAssertions;
using SonosStreaming.Core.Audio;
using Xunit;

namespace SonosStreaming.Tests;

// Issue #32: Sonos never implemented audio/L16. Querying ConnectionManager
// GetProtocolInfo on a One SL, a Beam and an Arc returns 65 sink formats and
// none of them is audio/L16, so the speaker connected, read one chunk while
// trying to identify the stream, and closed. The enum member survives only so
// an existing settings.json still deserializes.
public sealed class RetiredFormatTests
{
    [Fact]
    public void L16_IsNotOffered()
    {
        StreamingFormatExtensions.Selectable.Should().NotContain(StreamingFormat.L16Pcm);
    }

    [Fact]
    public void EverySelectableFormatIsSupportedBySonos()
    {
        // The sink list on every model tested advertises audio/aac and
        // audio/wav, and nothing else we stream.
        foreach (var fmt in StreamingFormatExtensions.Selectable)
            fmt.ContentType().Should().BeOneOf("audio/aac", "audio/wav");
    }

    [Fact]
    public void Normalize_MapsL16ToWav()
    {
        StreamingFormat.L16Pcm.Normalize().Should().Be(StreamingFormat.WavPcm);
    }

    [Theory]
    [InlineData(StreamingFormat.Aac128)]
    [InlineData(StreamingFormat.Aac192)]
    [InlineData(StreamingFormat.Aac256)]
    [InlineData(StreamingFormat.Aac320)]
    [InlineData(StreamingFormat.WavPcm)]
    public void Normalize_LeavesSupportedFormatsAlone(StreamingFormat fmt)
    {
        fmt.Normalize().Should().Be(fmt);
    }

    [Fact]
    public void AStaleL16Value_StillServesSomethingSonosCanPlay()
    {
        // Defence in depth: if any path misses the normalization, the stream
        // must degrade to WAV rather than to silence.
        StreamingFormat.L16Pcm.ContentType().Should().Be("audio/wav");
        StreamingFormat.L16Pcm.MetadataMimeType().Should().Be("audio/wav");
        StreamingFormat.L16Pcm.FileExtension().Should().Be(".wav");
        StreamingFormat.L16Pcm.IsPcm().Should().BeTrue();
    }
}
