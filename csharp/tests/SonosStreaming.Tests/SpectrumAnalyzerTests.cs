using FluentAssertions;
using SonosStreaming.Core.Audio.Dsp;
using Xunit;

namespace SonosStreaming.Tests;

public sealed class SpectrumAnalyzerTests
{
    [Fact]
    public void Process_SmoothsSpectrumRelease()
    {
        var analyzer = new SpectrumAnalyzer();
        analyzer.Process(SineFrame(1000f), 2);
        var firstPeak = analyzer.BandLevels.ToArray().Max();

        analyzer.Process(new float[2048 * 2], 2);
        var secondPeak = analyzer.BandLevels.ToArray().Max();

        firstPeak.Should().BeGreaterThan(0.01f);
        secondPeak.Should().BeLessThan(firstPeak);
        secondPeak.Should().BeGreaterThan(0f);
    }

    private static float[] SineFrame(float frequency)
    {
        var samples = new float[2048 * 2];
        for (int i = 0; i < 2048; i++)
        {
            var sample = MathF.Sin(2f * MathF.PI * frequency * i / 48000f);
            samples[i * 2] = sample;
            samples[i * 2 + 1] = sample;
        }

        return samples;
    }
}
