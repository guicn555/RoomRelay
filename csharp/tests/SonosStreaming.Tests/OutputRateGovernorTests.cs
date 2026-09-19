using FluentAssertions;
using SonosStreaming.Core.Pipeline;
using Xunit;

namespace SonosStreaming.Tests;

// Issue #26: the PCM stream delivered roughly 1.2% less audio than real time,
// so Sonos's jitter buffer drained and playback stalled after a minute or so.
// The governor holds the output to the wall clock.
public sealed class OutputRateGovernorTests
{
    private const uint Rate = 48000;

    private sealed class FakeClock
    {
        private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public DateTime Now() => _now;
        public void Advance(double ms) => _now = _now.AddMilliseconds(ms);
    }

    private static (OutputRateGovernor Governor, FakeClock Clock) Build()
    {
        var clock = new FakeClock();
        var governor = new OutputRateGovernor(Rate, clock.Now);
        governor.Start();
        return (governor, clock);
    }

    [Fact]
    public void BeforeStart_DoesNothing()
    {
        var clock = new FakeClock();
        var governor = new OutputRateGovernor(Rate, clock.Now);

        clock.Advance(5000);

        governor.IsRunning.Should().BeFalse();
        governor.PadFrames().Should().Be(0);
        governor.Accept(480).Should().BeFalse();
        governor.SkewMs.Should().Be(0);
    }

    [Fact]
    public void AfterStart_ASilentSourcePadsFromTimeZero()
    {
        // WASAPI loopback delivers nothing at all while the endpoint has no
        // active stream, so a silent PC must still produce a real-time stream
        // of silence rather than an empty response body.
        var (governor, clock) = Build();

        clock.Advance(1000);

        governor.PadFrames().Should().Be((int)Rate);
        governor.SkewMs.Should().BeApproximately(0, 1);
    }

    [Fact]
    public void ImmediatelyAfterStart_NothingIsPadded()
    {
        var (governor, _) = Build();

        governor.Accept(480).Should().BeTrue();

        governor.IsRunning.Should().BeTrue();
        governor.PadFrames().Should().Be(0);
        governor.PaddedFrames.Should().Be(0);
    }

    [Fact]
    public void WhenCaptureKeepsUp_NothingIsPaddedOrTrimmed()
    {
        var (governor, clock) = Build();

        // 200 buffers of exactly 10 ms each, with the clock advancing to match.
        for (int i = 0; i < 200; i++)
        {
            governor.Accept(480).Should().BeTrue();
            governor.PadFrames().Should().Be(0);
            clock.Advance(10);
        }

        governor.PaddedFrames.Should().Be(0);
        governor.TrimmedFrames.Should().Be(0);
    }

    [Fact]
    public void WhenCaptureUnderDelivers_PaddingRestoresRealTime()
    {
        var (governor, clock) = Build();
        governor.Accept(480);

        // Capture stalls for a second: no frames arrive at all.
        clock.Advance(1000);
        int pad = governor.PadFrames();

        pad.Should().BeGreaterThan(0);
        governor.SkewMs.Should().BeApproximately(0, 1);
    }

    [Fact]
    public void SmallShortfall_IsLeftAloneUntilItPassesTheThreshold()
    {
        var (governor, clock) = Build();
        governor.Accept(480);

        // The accepted frame carries 10 ms, so the clock has to move 10 ms just
        // to break even. The deadband is deliberately wider than the jitter a
        // descheduled capture thread introduces, so ordinary scheduling noise
        // does not trigger a pad.
        clock.Advance(100);   // 90 ms behind, inside the 150 ms deadband
        governor.PadFrames().Should().Be(0);

        clock.Advance(100);   // now 190 ms behind
        governor.PadFrames().Should().BeGreaterThan(0);
        governor.SkewMs.Should().BeApproximately(0, 1);
    }

    [Fact]
    public void SustainedDeficit_ConvergesInsteadOfAccumulating()
    {
        var (governor, clock) = Build();

        // Reproduces the reported shape: every buffer carries 10 ms of audio but
        // takes 10.12 ms of wall time, a 1.2% shortfall. Without the governor
        // this drifts out by ~600 ms per minute.
        for (int i = 0; i < 6000; i++)
        {
            if (governor.Accept(480)) { }
            governor.PadFrames();
            clock.Advance(10.12);
        }

        // Bounded by the deadband rather than drifting out by ~600 ms per
        // minute, which is what killed the stream in issue #26.
        governor.SkewMs.Should().BeLessThan(175);
        governor.PaddedMs.Should().BeGreaterThan(500);
    }

    [Fact]
    public void JitterInsideTheDeadband_NeverPads()
    {
        // A capture thread that runs late and then catches up must not provoke
        // a pad: padding for audio that is merely late leaves the stream ahead
        // of the clock once it arrives, until the trim drops real audio.
        var (governor, clock) = Build();

        for (int i = 0; i < 500; i++)
        {
            // Ten buffers' worth arrives in one burst after a 100 ms gap.
            clock.Advance(100);
            for (int j = 0; j < 10; j++) governor.Accept(480);
            governor.PadFrames().Should().Be(0);
        }

        governor.PaddedFrames.Should().Be(0);
        governor.TrimmedFrames.Should().Be(0);
    }

    [Fact]
    public void WhenOutputRunsAhead_ABufferIsSkipped()
    {
        var (governor, _) = Build();

        // One second of audio with the clock standing still.
        governor.Accept((int)Rate).Should().BeTrue();

        governor.SkewMs.Should().BeApproximately(-1000, 1);
        governor.Accept(480).Should().BeFalse();
        governor.TrimmedFrames.Should().Be(480);
        governor.ProducedFrames.Should().Be(Rate);
    }

    [Fact]
    public void RunningAhead_StabilisesAtTheTrimThreshold()
    {
        var (governor, clock) = Build();

        for (int i = 0; i < 200; i++)
        {
            governor.Accept(480);
            clock.Advance(1);
        }

        // Every buffer carries 10 ms but only 1 ms of wall time passes, so the
        // governor keeps skipping and the lead settles at the threshold.
        governor.SkewMs.Should().BeApproximately(-250, 11);
        governor.TrimmedFrames.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SkippingABuffer_LetsTheLeadShrinkOnItsOwn()
    {
        var (governor, clock) = Build();
        governor.Accept((int)Rate);
        governor.Accept(480).Should().BeFalse();

        clock.Advance(1500);

        governor.Accept(480).Should().BeTrue();
    }

    [Fact]
    public void PadFrames_IsCappedPerCall()
    {
        var clock = new FakeClock();
        var governor = new OutputRateGovernor(Rate, clock.Now, maxPadPerCallMs: 100);
        governor.Start();
        governor.Accept(480);

        clock.Advance(5000);

        governor.PadFrames().Should().Be((int)(Rate / 10)); // 100 ms
    }

    [Fact]
    public void Stop_FreezesTheClockButKeepsTheCounters()
    {
        // Teardown disposes the capture source before the pump notices, so a
        // still-running clock would bill the session for phantom debt and the
        // closing diagnostics would read like a starving stream.
        var (governor, clock) = Build();
        governor.Accept(480);
        clock.Advance(1000);
        governor.PadFrames();
        var paddedBefore = governor.PaddedFrames;

        governor.Stop();
        clock.Advance(30000);

        governor.IsRunning.Should().BeFalse();
        governor.SkewMs.Should().Be(0);
        governor.PaddedFrames.Should().Be(paddedBefore);
        governor.ProducedFrames.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Reset_ClearsCountersAndUnanchorsTheClock()
    {
        var (governor, clock) = Build();
        governor.Accept(480);
        clock.Advance(1000);
        governor.PadFrames();

        governor.Reset();

        governor.IsRunning.Should().BeFalse();
        governor.ProducedFrames.Should().Be(0);
        governor.PaddedFrames.Should().Be(0);
        governor.TrimmedFrames.Should().Be(0);
    }

    [Fact]
    public void Accept_IgnoresEmptyFrames()
    {
        var (governor, _) = Build();

        governor.Accept(0).Should().BeFalse();

        governor.ProducedFrames.Should().Be(0);
        governor.TrimmedFrames.Should().Be(0);
    }
}
