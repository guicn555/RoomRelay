using FluentAssertions;
using SonosStreaming.Core.Pipeline;
using Xunit;

namespace SonosStreaming.Tests;

// The pump's stall timeout decides when the rate governor is allowed to treat
// capture as idle and pad the gap. If it fires while a buffer is merely late,
// the governor pads for audio that then arrives and is encoded anyway, and the
// stream ends up permanently ahead of real time by the padded amount.
public sealed class StallTimeoutTests
{
    [Theory]
    [InlineData(StreamingLatencyMode.Stable)]
    [InlineData(StreamingLatencyMode.LowLatency)]
    public void StallTimeout_ExceedsTheCaptureBuffer(StreamingLatencyMode mode)
    {
        // Observed as a staircase in clockSkew: -46 ms, then -112, then -202,
        // one step per padding event, because the old fixed 100 ms timeout was
        // shorter than Stable mode's 200 ms capture buffer.
        mode.StallTimeoutMs().Should().BeGreaterThan(mode.CaptureBufferMs());
    }

    [Theory]
    [InlineData(StreamingLatencyMode.Stable)]
    [InlineData(StreamingLatencyMode.LowLatency)]
    public void StallTimeout_LeavesHeadroomOverTheCaptureBuffer(StreamingLatencyMode mode)
    {
        // A buffer can legitimately arrive a full period late under load, so
        // one period of slack is not enough.
        mode.StallTimeoutMs().Should().BeGreaterThanOrEqualTo(mode.CaptureBufferMs() * 2);
    }

    [Theory]
    [InlineData(StreamingLatencyMode.Stable)]
    [InlineData(StreamingLatencyMode.LowLatency)]
    public void StallTimeout_StaysShortEnoughToKeepTheStreamFlowing(StreamingLatencyMode mode)
    {
        // A silent PC produces no buffers at all, so this is how long the
        // stream pauses before silence starts flowing. Sonos buffers seconds,
        // but this should stay well inside that.
        mode.StallTimeoutMs().Should().BeLessThanOrEqualTo(1000);
    }

    [Fact]
    public void StallTimeout_HasAFloorForVerySmallCaptureBuffers()
    {
        // LowLatency's 50 ms buffer would otherwise give a 100 ms timeout,
        // which is the value that caused the bug in the first place.
        StreamingLatencyMode.LowLatency.StallTimeoutMs().Should().Be(300);
    }

    [Fact]
    public void StallTimeout_ScalesWithStableModesLargerBuffer()
    {
        StreamingLatencyMode.Stable.StallTimeoutMs().Should().Be(400);
    }
}
