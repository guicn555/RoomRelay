using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using SonosStreaming.Core.Audio;

namespace SonosStreaming.Core.State;

public sealed class AppSettings : IDisposable
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RoomRelay");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Timer _saveDebounceTimer;
    private readonly Lock _saveLock = new();
    private bool _disposed;

    public float GainL { get; set; } = 1.0f;
    public float GainR { get; set; } = 1.0f;
    public float Volume { get; set; } = 1.0f;
    public float Balance { get; set; } = 0.0f;
    public float EqLowDb { get; set; } = 0.0f;
    public float EqMidDb { get; set; } = 0.0f;
    public float EqHighDb { get; set; } = 0.0f;
    public float DelayMsL { get; set; } = 0.0f;
    public float DelayMsR { get; set; } = 0.0f;
    public string? LastSpeakerUdn { get; set; }
    public StreamingFormat StreamingFormat { get; set; } = StreamingFormat.Aac256;
    public ThemePreference ThemePreference { get; set; } = ThemePreference.System;
    public List<ManualSpeakerEndpoint> ManualSpeakerEndpoints { get; set; } = new();

    public AppSettings()
    {
        _saveDebounceTimer = new Timer(_ => FlushSave(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            settings.ManualSpeakerEndpoints ??= [];
            return settings;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Schedules a save after a 500 ms debounce. Callers can invoke this
    /// freely (e.g. on every slider tick) without flooding the disk.
    /// </summary>
    public void Save()
    {
        if (_disposed) return;
        _saveDebounceTimer.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
    }

    private void FlushSave()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(this, JsonOpts);
                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to save settings");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveDebounceTimer.Dispose();
        FlushSave();
    }
}

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed record ManualSpeakerEndpoint
{
    public string Ip { get; init; } = "";
    public ushort Port { get; init; } = 1400;
    public string DisplayName => $"{Ip}:{Port}";
}
