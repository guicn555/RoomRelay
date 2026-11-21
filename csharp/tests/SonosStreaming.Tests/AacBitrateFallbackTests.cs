using System.Runtime.InteropServices;
using FluentAssertions;
using SonosStreaming.Core.Audio;
using Xunit;

namespace SonosStreaming.Tests;

// Issue #35: the Windows 10 22H2 AAC encoder MFT rejects 256 kbps with
// MF_E_INVALIDMEDIATYPE, while Windows 11 accepts it. The encoder must step
// down to a rate the MFT accepts instead of failing the stream.
public sealed class AacBitrateFallbackTests
{
    private const int MF_E_INVALIDMEDIATYPE = unchecked((int)0xC00D36B4);

    // Rates the Windows 10 22H2 encoder accepts for 48 kHz stereo, per the
    // Microsoft documentation and the issue #35 report (192 kbps works).
    private static readonly HashSet<int> Windows10Rates = [96_000, 128_000, 160_000, 192_000];

    private static Action<int> AcceptOnly(ISet<int> accepted, List<int>? tried = null) => rate =>
    {
        tried?.Add(rate);
        if (!accepted.Contains(rate))
            throw new COMException("The data specified for the media type is invalid", MF_E_INVALIDMEDIATYPE);
    };

    [Fact]
    public void Candidates_StartWithTheRequestedRateAndStepDown()
    {
        AacBitrateFallback.Candidates(256_000).Should().Equal(256_000, 192_000, 160_000, 128_000, 96_000);
        AacBitrateFallback.Candidates(320_000).Should().Equal(320_000, 256_000, 192_000, 160_000, 128_000, 96_000);
    }

    [Fact]
    public void Candidates_ForTheLowestRate_IsJustThatRate()
    {
        AacBitrateFallback.Candidates(96_000).Should().Equal(96_000);
    }

    [Fact]
    public void Candidates_ForAnUnlistedRate_TryItFirstThenTheKnownRatesBelow()
    {
        AacBitrateFallback.Candidates(200_000).Should().Equal(200_000, 192_000, 160_000, 128_000, 96_000);
    }

    [Theory]
    [InlineData(StreamingFormat.Aac128)]
    [InlineData(StreamingFormat.Aac192)]
    [InlineData(StreamingFormat.Aac256)]
    [InlineData(StreamingFormat.Aac320)]
    public void EveryAacFormatIsAKnownRate(StreamingFormat fmt)
    {
        AacBitrateFallback.KnownBitRates.Should().Contain(fmt.Bitrate());
    }

    [Fact]
    public void Negotiate_UsesTheRequestedRateWhenAccepted()
    {
        var tried = new List<int>();
        AacBitrateFallback.Negotiate(256_000, AcceptOnly(new HashSet<int>(AacBitrateFallback.KnownBitRates), tried))
            .Should().Be(256_000);
        tried.Should().Equal(256_000);
    }

    [Theory]
    [InlineData(256_000)]
    [InlineData(320_000)]
    public void Negotiate_OnWindows10_FallsBackTo192(int requested)
    {
        var tried = new List<int>();
        AacBitrateFallback.Negotiate(requested, AcceptOnly(Windows10Rates, tried)).Should().Be(192_000);
        tried.Last().Should().Be(192_000);
        tried.First().Should().Be(requested);
    }

    [Theory]
    [InlineData(128_000)]
    [InlineData(192_000)]
    public void Negotiate_OnWindows10_LeavesSupportedRatesAlone(int requested)
    {
        AacBitrateFallback.Negotiate(requested, AcceptOnly(Windows10Rates)).Should().Be(requested);
    }

    [Fact]
    public void Negotiate_WhenEveryRateIsRejected_ThrowsAReadableError()
    {
        var act = () => AacBitrateFallback.Negotiate(256_000, AcceptOnly(new HashSet<int>()));

        var thrown = act.Should().Throw<InvalidOperationException>().Which;
        thrown.Message.Should().Contain("256, 192, 160, 128, 96 kbps").And.Contain("0xC00D36B4").And.Contain("WAV PCM");
        thrown.InnerException.Should().BeOfType<COMException>()
            .Which.HResult.Should().Be(MF_E_INVALIDMEDIATYPE);
    }

    [Fact]
    public void Negotiate_DoesNotTreatOtherFailuresAsABitrateRejection()
    {
        var tried = new List<int>();
        var act = () => AacBitrateFallback.Negotiate(256_000, rate =>
        {
            tried.Add(rate);
            throw new ObjectDisposedException("encoder");
        });

        act.Should().Throw<ObjectDisposedException>();
        tried.Should().Equal(256_000);
    }
}
