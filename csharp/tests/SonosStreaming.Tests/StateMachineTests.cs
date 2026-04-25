using SonosStreaming.Core.State;
using SonosStreaming.Core.Network;
using FluentAssertions;
using Xunit;
using System.Net;

namespace SonosStreaming.Tests;

public class StateMachineTests
{
    private static SonosDevice FakeSpeaker() => new("Kitchen", IPAddress.Parse("192.168.1.42"), 1400, "uuid:RINCON_FAKE");

    [Fact]
    public void New_IsIdle_CannotStart()
    {
        var core = new AppCore();
        core.State.Should().Be(AppState.Idle);
        core.CanStart.Should().BeFalse();
        core.CanStop.Should().BeFalse();
    }

    [Fact]
    public void SelectingSpeaker_EnablesStart()
    {
        var core = new AppCore();
        core.SetSpeaker(FakeSpeaker());
        core.CanStart.Should().BeTrue();
    }

    [Fact]
    public void FullStartStopCycle()
    {
        var core = new AppCore();
        core.SetSpeaker(FakeSpeaker());

        core.BeginStart();
        core.State.Should().Be(AppState.Starting);
        core.CanStart.Should().BeFalse();

        core.FinishStart();
        core.State.Should().Be(AppState.Streaming);
        core.CanStop.Should().BeTrue();

        core.BeginStop();
        core.State.Should().Be(AppState.Stopping);

        core.FinishStop();
        core.State.Should().Be(AppState.Idle);
    }

    [Fact]
    public void CannotChangeSourceWhileStreaming()
    {
        var core = new AppCore();
        core.SetSpeaker(FakeSpeaker());
        core.BeginStart();
        core.FinishStart();

        var act = () => core.SetSource(AudioSourceSelection.Process, new AudioSourceProcessSelection { Pid = 1234, Name = "test.exe" });
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void StopDuringStarting_IsAllowed()
    {
        var core = new AppCore();
        core.SetSpeaker(FakeSpeaker());
        core.BeginStart();
        core.BeginStop();
        core.FinishStop();
        core.State.Should().Be(AppState.Idle);
    }

    [Fact]
    public void DoubleStart_Rejected()
    {
        var core = new AppCore();
        core.SetSpeaker(FakeSpeaker());
        core.BeginStart();
        var act = () => core.BeginStart();
        act.Should().Throw<InvalidOperationException>();
    }
}
