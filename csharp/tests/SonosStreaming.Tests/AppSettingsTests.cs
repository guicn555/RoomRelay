using SonosStreaming.Core.Audio;
using SonosStreaming.Core.Pipeline;
using SonosStreaming.Core.State;
using FluentAssertions;
using Xunit;

namespace SonosStreaming.Tests;

public class AppSettingsTests : IDisposable
{
    private readonly string _settingsDir;
    private readonly string _settingsPath;

    public AppSettingsTests()
    {
        _settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RoomRelay");
        _settingsPath = Path.Combine(_settingsDir, "settings.json");

        // Clean slate
        if (File.Exists(_settingsPath))
            File.Delete(_settingsPath);
    }

    public void Dispose()
    {
        try { if (File.Exists(_settingsPath)) File.Delete(_settingsPath); } catch { }
    }

    [Fact]
    public void Load_WithNoFile_ReturnsDefaults()
    {
        var s = AppSettings.Load();
        s.Volume.Should().Be(1.0f);
        s.GainL.Should().Be(1.0f);
        s.GainR.Should().Be(1.0f);
        s.LastSpeakerUdn.Should().BeNull();
    }

    [Fact]
    public void Save_ThenLoad_RoundtripsValues()
    {
        var original = new AppSettings
        {
            Volume = 3.5f,
            GainL = 1.2f,
            GainR = 0.8f,
            EqLowDb = -3.0f,
            EqMidDb = 2.0f,
            EqHighDb = 1.5f,
            DelayMsL = 10.0f,
            DelayMsR = 5.0f,
            LastSpeakerUdn = "uuid:test-speaker",
            LastProcessName = "vlc",
            LatencyMode = StreamingLatencyMode.LowLatency,
            ProcessPreferences =
            [
                new AppProcessPreference { ProcessName = "vlc", StreamingFormat = StreamingFormat.WavPcm, LatencyMode = StreamingLatencyMode.LowLatency },
            ],
            ManualSpeakerEndpoints =
            [
                new ManualSpeakerEndpoint { Ip = "192.168.1.50", Port = 1400 },
                new ManualSpeakerEndpoint { Ip = "192.168.1.51", Port = 1500 },
            ]
        };

        original.Save();
        // Wait for debounce timer (500 ms) + margin
        Thread.Sleep(700);

        var loaded = AppSettings.Load();
        loaded.Volume.Should().Be(3.5f);
        loaded.GainL.Should().Be(1.2f);
        loaded.GainR.Should().Be(0.8f);
        loaded.EqLowDb.Should().Be(-3.0f);
        loaded.EqMidDb.Should().Be(2.0f);
        loaded.EqHighDb.Should().Be(1.5f);
        loaded.DelayMsL.Should().Be(10.0f);
        loaded.DelayMsR.Should().Be(5.0f);
        loaded.LastSpeakerUdn.Should().Be("uuid:test-speaker");
        loaded.LastProcessName.Should().Be("vlc");
        loaded.LatencyMode.Should().Be(StreamingLatencyMode.LowLatency);
        loaded.ProcessPreferences.Should().BeEquivalentTo(original.ProcessPreferences);
        loaded.ManualSpeakerEndpoints.Should().BeEquivalentTo(original.ManualSpeakerEndpoints);
    }

    [Fact]
    public void MultipleRapidSaves_DoNotThrow()
    {
        var settings = new AppSettings();
        for (int i = 0; i < 50; i++)
        {
            settings.Volume = i * 0.1f;
            settings.Save();
        }
        // Dispose flushes any pending save
        settings.Dispose();

        // File should exist and contain the last (or near-last) value
        File.Exists(_settingsPath).Should().BeTrue();
    }

    [Fact]
    public void Dispose_FlushesPendingSave()
    {
        var settings = new AppSettings { Volume = 7.77f };
        settings.Save();
        // Do NOT wait for timer — Dispose should flush synchronously
        settings.Dispose();

        var loaded = AppSettings.Load();
        loaded.Volume.Should().Be(7.77f);
    }

    [Fact]
    public void Save_ThenLoad_RoundtripsStreamingFormat()
    {
        var original = new AppSettings { StreamingFormat = StreamingFormat.WavPcm };
        original.Save();
        Thread.Sleep(700);
        var loaded = AppSettings.Load();
        loaded.StreamingFormat.Should().Be(StreamingFormat.WavPcm);
        original.Dispose();
    }

    [Fact]
    public void Save_ThenLoad_RoundtripsThemePreference()
    {
        var original = new AppSettings { ThemePreference = ThemePreference.Dark };
        original.Save();
        Thread.Sleep(700);

        var loaded = AppSettings.Load();

        loaded.ThemePreference.Should().Be(ThemePreference.Dark);
        original.Dispose();
    }

    [Fact]
    public void Load_WithNoFile_DefaultsToStableLatency()
    {
        var s = AppSettings.Load();

        s.LatencyMode.Should().Be(StreamingLatencyMode.Stable);
        s.ProcessPreferences.Should().BeEmpty();
    }
}
