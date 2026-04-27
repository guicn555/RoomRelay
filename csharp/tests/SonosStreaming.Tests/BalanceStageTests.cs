using FluentAssertions;
using SonosStreaming.Core.Audio.Dsp;
using Xunit;

namespace SonosStreaming.Tests;

public sealed class BalanceStageTests
{
    [Fact]
    public void Apply_PositiveBalance_AttenuatesLeftChannel()
    {
        var stage = new BalanceStage { Balance = 0.25f };
        float[] samples = [1f, 1f, 0.5f, 0.5f];

        stage.Apply(samples, 2);

        samples.Should().Equal(0.75f, 1f, 0.375f, 0.5f);
    }

    [Fact]
    public void Apply_NegativeBalance_AttenuatesRightChannel()
    {
        var stage = new BalanceStage { Balance = -0.5f };
        float[] samples = [1f, 1f, 0.5f, 0.5f];

        stage.Apply(samples, 2);

        samples.Should().Equal(1f, 0.5f, 0.5f, 0.25f);
    }
}
