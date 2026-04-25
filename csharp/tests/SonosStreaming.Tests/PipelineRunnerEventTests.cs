using SonosStreaming.Core.Pipeline;
using SonosStreaming.Core.State;
using FluentAssertions;
using Xunit;

namespace SonosStreaming.Tests;

public class PipelineRunnerEventTests
{
    [Fact]
    public void Constructor_HasIdleState()
    {
        var core = new AppCore();
        var runner = new PipelineRunner(core);
        runner.CurrentMixFormat.Should().BeNull();
    }

    [Fact]
    public void PumpCrashed_CanSubscribeAndUnsubscribe()
    {
        var core = new AppCore();
        var runner = new PipelineRunner(core);

        Exception? captured = null;
        EventHandler<Exception> handler = (_, ex) => captured = ex;

        runner.PumpCrashed += handler;
        runner.PumpCrashed -= handler;

        // Just verify subscription/unsubscription doesn't throw
        captured.Should().BeNull();
    }

    [Fact]
    public void FormatChanged_CanSubscribeAndUnsubscribe()
    {
        var core = new AppCore();
        var runner = new PipelineRunner(core);

        bool fired = false;
        EventHandler handler = (_, _) => fired = true;

        runner.FormatChanged += handler;
        runner.FormatChanged -= handler;

        fired.Should().BeFalse();
    }

    [Fact]
    public void ClientCount_DefaultsToZero()
    {
        var core = new AppCore();
        var runner = new PipelineRunner(core);

        // ClientCount is internal set; verify default through reflection
        var prop = typeof(PipelineRunner).GetProperty("ClientCount");
        prop.Should().NotBeNull();
        var value = prop!.GetValue(runner);
        value.Should().Be(0);
    }
}
