using SonosStreaming.Core.Audio;
using FluentAssertions;
using Xunit;

namespace SonosStreaming.Tests;

public class StreamingFormatTests
{
    [Theory]
    [InlineData(StreamingFormat.Aac128, 128_000)]
    [InlineData(StreamingFormat.Aac192, 192_000)]
    [InlineData(StreamingFormat.Aac256, 256_000)]
    [InlineData(StreamingFormat.Aac320, 320_000)]
    [InlineData(StreamingFormat.Lpcm, 0)]
    public void Bitrate_ReturnsExpectedValue(StreamingFormat fmt, int expected)
    {
        fmt.Bitrate().Should().Be(expected);
    }

    [Theory]
    [InlineData(StreamingFormat.Aac128, "AAC 128 kbps")]
    [InlineData(StreamingFormat.Aac192, "AAC 192 kbps")]
    [InlineData(StreamingFormat.Aac256, "AAC 256 kbps")]
    [InlineData(StreamingFormat.Aac320, "AAC 320 kbps")]
    [InlineData(StreamingFormat.Lpcm, "LPCM/WAV lossless (experimental)")]
    public void DisplayName_ReturnsExpectedLabel(StreamingFormat fmt, string expected)
    {
        fmt.DisplayName().Should().Be(expected);
    }

    [Theory]
    [InlineData(StreamingFormat.Aac128, "audio/aac")]
    [InlineData(StreamingFormat.Aac192, "audio/aac")]
    [InlineData(StreamingFormat.Aac256, "audio/aac")]
    [InlineData(StreamingFormat.Aac320, "audio/aac")]
    [InlineData(StreamingFormat.Lpcm, "audio/wav")]
    public void ContentType_ReturnsExpectedMimeType(StreamingFormat fmt, string expected)
    {
        fmt.ContentType().Should().Be(expected);
    }

    [Theory]
    [InlineData(StreamingFormat.Aac128, ".aac")]
    [InlineData(StreamingFormat.Aac192, ".aac")]
    [InlineData(StreamingFormat.Aac256, ".aac")]
    [InlineData(StreamingFormat.Aac320, ".aac")]
    [InlineData(StreamingFormat.Lpcm, ".wav")]
    public void FileExtension_ReturnsExpectedExtension(StreamingFormat fmt, string expected)
    {
        fmt.FileExtension().Should().Be(expected);
    }
}
