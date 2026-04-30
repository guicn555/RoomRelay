using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using SonosStreaming.App.Services;
using SonosStreaming.Core.Audio;
using SonosStreaming.Core.Network;
using SonosStreaming.Core.Pipeline;
using SonosStreaming.Core.State;
using Serilog;

namespace SonosStreaming.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppCore _core;
    private readonly DiagnosticsPackageService _diagnostics;
    public PipelineRunner Pipeline { get; }
    public AppSettings Settings { get; }

    public ObservableCollection<SonosDevice> Speakers { get; } = new();
    public ObservableCollection<ManualSpeakerEndpoint> SavedManualSpeakerEndpoints { get; } = new();

    [ObservableProperty]
    public partial SonosDevice? SelectedSpeaker { get; set; }

    public ObservableCollection<AudioProcess> AudioProcesses { get; } = new();

    public AudioProcessEnumerationResult? LastAudioProcessEnumeration { get; private set; }

    [ObservableProperty]
    public partial string StateLabel { get; set; }

    [ObservableProperty]
    public partial string InputFormatLabel { get; set; }

    [ObservableProperty]
    public partial int ClientCount { get; set; }

    public string ClientStatusLabel => StateLabel == "Streaming"
        ? ClientCount switch
        {
            0 => "Waiting for speaker...",
            1 => "1 Sonos client connected",
            _ => $"{ClientCount} Sonos clients connected",
        } + (SelectedSpeaker != null ? $" ({SelectedSpeaker.Ip})" : "")
        : "No Sonos client connected";

    [ObservableProperty]
    public partial float VuL { get; set; }

    [ObservableProperty]
    public partial float VuR { get; set; }

    [ObservableProperty]
    public partial float[] Spectrum { get; set; }

    [ObservableProperty]
    public partial bool IsClipping { get; set; }

    public string ClippingStatusLabel => IsClipping
        ? "Clipping detected. Lower the source app volume, stream volume, or gain."
        : "";

    [ObservableProperty]
    public partial float Volume { get; set; }

    [ObservableProperty]
    public partial float Balance { get; set; }

    [ObservableProperty]
    public partial float GainL { get; set; }

    [ObservableProperty]
    public partial float GainR { get; set; }

    [ObservableProperty]
    public partial bool GainLinked { get; set; }

    [ObservableProperty]
    public partial float EqLowDb { get; set; }

    [ObservableProperty]
    public partial float EqMidDb { get; set; }

    [ObservableProperty]
    public partial float EqHighDb { get; set; }

    [ObservableProperty]
    public partial float DelayMsL { get; set; }

    [ObservableProperty]
    public partial float DelayMsR { get; set; }

    [ObservableProperty]
    public partial AudioProcess? SelectedProcess { get; set; }

    [ObservableProperty]
    public partial bool ShowAllAudioSessions { get; set; }

    [ObservableProperty]
    public partial string AudioProcessStatus { get; set; }

    [ObservableProperty]
    public partial bool IsProcessSourceSelected { get; set; }

    [ObservableProperty]
    public partial bool IsErrorVisible { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string NotificationTitle { get; set; }

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Controls.InfoBarSeverity NotificationSeverity { get; set; }

    [ObservableProperty]
    public partial StreamingFormat SelectedFormat { get; set; }

    [ObservableProperty]
    public partial StreamingLatencyMode SelectedLatencyMode { get; set; }

    [ObservableProperty]
    public partial string EncoderLabel { get; set; }

    [ObservableProperty]
    public partial string ManualSpeakerIp { get; set; }

    [ObservableProperty]
    public partial string ManualSpeakerPort { get; set; }

    [ObservableProperty]
    public partial string ManualSpeakerStatus { get; set; }

    [ObservableProperty]
    public partial ManualSpeakerEndpoint? SelectedManualSpeakerEndpoint { get; set; }

    [ObservableProperty]
    public partial string DiscoveryStatus { get; set; }

    [ObservableProperty]
    public partial string DiagnosticsStatus { get; set; }

    [ObservableProperty]
    public partial double SonosVolume { get; set; }

    [ObservableProperty]
    public partial string SonosVolumeStatus { get; set; }

    [ObservableProperty]
    public partial ThemePreference ThemePreference { get; set; }

    public event EventHandler? ThemePreferenceChanged;

    public string SourceStatusLabel
    {
        get
        {
            if (IsProcessSourceSelected && SelectedProcess == null)
                return "Select an application before starting per-application capture.";
            if (IsProcessSourceSelected)
                return "If capture fails, the app may be protected, elevated, browser-isolated, or not producing audio yet. Switch to Whole system as fallback.";
            return "";
        }
    }

    public bool IsWholeSystemSourceSelected => !IsProcessSourceSelected;

    public string AppVersionLabel => $"RoomRelay {DiagnosticsPackageService.VersionLabel}";

    public string SelectedSpeakerLabel => SelectedSpeaker?.FriendlyName ?? "No room selected";

    [ObservableProperty]
    public partial string PlaybackTargetLabel { get; set; }

    private static readonly StreamingFormat[] _visibleFormats = Enum.GetValues<StreamingFormat>();
    public StreamingFormat[] AvailableFormats { get; } = _visibleFormats;
    public string[] AvailableFormatNames { get; } = _visibleFormats.Select(f => f.DisplayName()).ToArray();
    private static readonly StreamingLatencyMode[] _visibleLatencyModes = Enum.GetValues<StreamingLatencyMode>();
    public string[] AvailableLatencyModeNames { get; } = _visibleLatencyModes.Select(m => m.DisplayName()).ToArray();

    public int SelectedFormatIndex
    {
        get => (int)SelectedFormat;
        set { if (value >= 0) SelectedFormat = (StreamingFormat)value; }
    }

    public int SelectedLatencyModeIndex
    {
        get => (int)SelectedLatencyMode;
        set { if (value >= 0) SelectedLatencyMode = (StreamingLatencyMode)value; }
    }

    public string LatencyModeHelp => SelectedLatencyMode == StreamingLatencyMode.LowLatency
        ? "Low latency uses smaller buffers for PCM/WAV and may be more sensitive to Wi-Fi or older Sonos hardware."
        : SelectedFormat.IsPcm()
            ? "Stable keeps larger buffers and is recommended for music, podcasts, radio, and unreliable Wi-Fi."
            : "AAC always uses Stable mode because Sonos and AAC buffering dominate latency. Use WAV/L16 PCM for low-latency mode.";
    public string LatencyModeLabel => SelectedLatencyMode.DisplayName();
    public bool CanSelectLatencyMode => SelectedFormat.IsPcm() && IsNotStreaming;

    public bool IsNotStreaming => StateLabel != "Streaming";

    private readonly ISsdpDiscovery _discovery;
    private readonly ITopologyResolver _topology;
    private DispatcherQueue? _dq;
    private Task? _processPollTask;
    private CancellationTokenSource? _processPollCts;
    private CancellationTokenSource? _sonosVolumeDebounceCts;
    private bool _allowSonosVolumeApply;
    private bool _updatingSonosVolumeFromDevice;
    private long _sonosVolumeUserVersion;

    public MainViewModel(AppCore core, PipelineRunner pipeline, AppSettings settings, ISsdpDiscovery discovery, ITopologyResolver topology, DiagnosticsPackageService diagnostics)
    {
        _core = core;
        _diagnostics = diagnostics;
        Pipeline = pipeline;
        Settings = settings;
        _discovery = discovery;
        _topology = topology;
        StateLabel = "Idle";
        InputFormatLabel = "—";
        Spectrum = new float[64];
        Volume = settings.Volume;
        Balance = settings.Balance;
        GainL = settings.GainL;
        GainR = settings.GainR;
        GainLinked = true;
        EqLowDb = settings.EqLowDb;
        EqMidDb = settings.EqMidDb;
        EqHighDb = settings.EqHighDb;
        DelayMsL = settings.DelayMsL;
        DelayMsR = settings.DelayMsR;
        AudioProcessStatus = "Looking for app audio sessions...";
        ErrorMessage = "";
        NotificationTitle = "";
        NotificationSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        SelectedFormat = settings.StreamingFormat;
        SelectedLatencyMode = settings.LatencyMode;
        EncoderLabel = settings.StreamingFormat.DisplayName();
        ManualSpeakerIp = "";
        ManualSpeakerPort = "1400";
        ManualSpeakerStatus = "";
        DiscoveryStatus = "";
        DiagnosticsStatus = "";
        SonosVolume = 0;
        SonosVolumeStatus = "Select a room to read or set Sonos volume.";
        PlaybackTargetLabel = "No room selected";
        ThemePreference = settings.ThemePreference;
        Pipeline.Format = settings.StreamingFormat;
        Pipeline.LatencyMode = settings.LatencyMode;
        SyncSavedManualEndpoints();

        // Apply persisted settings to pipeline stages so restart restores the
        // user's last tuning without them having to touch any sliders.
        Pipeline.GainStage.GainL = settings.GainL;
        Pipeline.GainStage.GainR = settings.GainR;
        Pipeline.BalanceStage.Balance = settings.Balance;
        Pipeline.VolumeStage.Volume = settings.Volume;
        Pipeline.Equalizer.LowGainDb = settings.EqLowDb;
        Pipeline.Equalizer.MidGainDb = settings.EqMidDb;
        Pipeline.Equalizer.HighGainDb = settings.EqHighDb;
        Pipeline.ChannelDelay.DelayMsL = settings.DelayMsL;
        Pipeline.ChannelDelay.DelayMsR = settings.DelayMsR;

        Pipeline.PumpCrashed += OnPumpCrashed;
        Pipeline.FormatChanged += OnFormatChanged;
        _allowSonosVolumeApply = true;
    }

    private async void OnFormatChanged(object? sender, EventArgs e)
    {
        Log.Warning("Audio endpoint format changed; stopping stream");
        var speaker = _core.Selection.Speaker;
        try { await Pipeline.StopAsync(speaker); } catch { }
        try { _core.BeginStop(); _core.FinishStop(); } catch { }
        ErrorMessage = "Audio format changed. Please restart the stream.";
        ShowNotification("Stream Error", ErrorMessage, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        StateLabel = "Idle";
    }

    partial void OnClientCountChanged(int value)
    {
        OnPropertyChanged(nameof(ClientStatusLabel));
    }

    partial void OnIsClippingChanged(bool value)
    {
        OnPropertyChanged(nameof(ClippingStatusLabel));
    }

    partial void OnVolumeChanged(float value)
    {
        Pipeline.VolumeStage.Volume = value;
        Settings.Volume = value;
        Settings.Save();
    }

    partial void OnBalanceChanged(float value)
    {
        Pipeline.BalanceStage.Balance = value;
        Settings.Balance = value;
        Settings.Save();
    }

    partial void OnGainLChanged(float value)
    {
        Pipeline.GainStage.GainL = value;
        Settings.GainL = value;
        Settings.Save();
        if (GainLinked && Math.Abs(GainR - value) > 0.001f)
            GainR = value;
    }

    partial void OnGainRChanged(float value)
    {
        Pipeline.GainStage.GainR = value;
        Settings.GainR = value;
        Settings.Save();
        if (GainLinked && Math.Abs(GainL - value) > 0.001f)
            GainL = value;
    }

    partial void OnEqLowDbChanged(float value)
    {
        Pipeline.Equalizer.LowGainDb = value;
        Settings.EqLowDb = value;
        Settings.Save();
    }

    partial void OnEqMidDbChanged(float value)
    {
        Pipeline.Equalizer.MidGainDb = value;
        Settings.EqMidDb = value;
        Settings.Save();
    }

    partial void OnEqHighDbChanged(float value)
    {
        Pipeline.Equalizer.HighGainDb = value;
        Settings.EqHighDb = value;
        Settings.Save();
    }

    partial void OnDelayMsLChanged(float value)
    {
        Pipeline.ChannelDelay.DelayMsL = value;
        Settings.DelayMsL = value;
        Settings.Save();
    }

    partial void OnDelayMsRChanged(float value)
    {
        Pipeline.ChannelDelay.DelayMsR = value;
        Settings.DelayMsR = value;
        Settings.Save();
    }

    partial void OnSelectedFormatChanged(StreamingFormat value)
    {
        if (!value.IsPcm() && SelectedLatencyMode != StreamingLatencyMode.Stable)
            SelectedLatencyMode = StreamingLatencyMode.Stable;

        Pipeline.Format = value;
        Settings.StreamingFormat = value;
        RememberSelectedProcessPreference();
        Settings.Save();
        EncoderLabel = value.DisplayName();
        OnPropertyChanged(nameof(SelectedFormatIndex));
        OnPropertyChanged(nameof(CanSelectLatencyMode));
        OnPropertyChanged(nameof(LatencyModeHelp));
    }

    partial void OnSelectedLatencyModeChanged(StreamingLatencyMode value)
    {
        Pipeline.LatencyMode = value;
        Settings.LatencyMode = value;
        RememberSelectedProcessPreference();
        Settings.Save();
        OnPropertyChanged(nameof(SelectedLatencyModeIndex));
        OnPropertyChanged(nameof(LatencyModeHelp));
        OnPropertyChanged(nameof(LatencyModeLabel));
        OnPropertyChanged(nameof(CanSelectLatencyMode));
    }

    partial void OnThemePreferenceChanged(ThemePreference value)
    {
        Settings.ThemePreference = value;
        Settings.Save();
        ThemePreferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnManualSpeakerIpChanged(string value)
    {
        ManualSpeakerStatus = "";
        AddManualSpeakerCommand.NotifyCanExecuteChanged();
    }

    partial void OnManualSpeakerPortChanged(string value)
    {
        ManualSpeakerStatus = "";
        AddManualSpeakerCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedManualSpeakerEndpointChanged(ManualSpeakerEndpoint? value)
    {
        RemoveManualSpeakerCommand.NotifyCanExecuteChanged();
    }

    partial void OnSonosVolumeChanged(double value)
    {
        if (!_allowSonosVolumeApply || _updatingSonosVolumeFromDevice)
            return;

        var speaker = SelectedSpeaker ?? _core.Selection.Speaker;
        if (speaker == null)
            return;

        _sonosVolumeDebounceCts?.Cancel();
        _sonosVolumeDebounceCts?.Dispose();
        _sonosVolumeDebounceCts = new CancellationTokenSource();
        var token = _sonosVolumeDebounceCts.Token;
        var volume = (int)Math.Round(Math.Clamp(value, 0, 100));
        var version = Interlocked.Increment(ref _sonosVolumeUserVersion);
        SetSonosVolumeStatus($"Setting Sonos volume to {volume}%...");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token).ConfigureAwait(false);
                await new SonosController().SetVolumeAsync(speaker, volume, token).ConfigureAwait(false);
                if (version == Interlocked.Read(ref _sonosVolumeUserVersion))
                    SetSonosVolumeStatus($"Set Sonos volume to {volume}%");
                Log.Information("Set Sonos volume for {Speaker} to {Volume}", speaker.FriendlyName, volume);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to set Sonos volume");
                SetSonosVolumeStatus("Could not set Sonos volume");
            }
        }, CancellationToken.None);
    }

    private void SetSonosVolumeStatus(string status)
    {
        if (_dq != null)
            _dq.TryEnqueue(() => SonosVolumeStatus = status);
        else
            SonosVolumeStatus = status;
    }

    partial void OnSelectedProcessChanged(AudioProcess? value)
    {
        StartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SourceStatusLabel));
        if (value != null)
        {
            Settings.LastProcessName = value.Name;
            ApplyProcessPreference(value.Name);
            Settings.Save();
            try { _core.SetSource(AudioSourceSelection.Process, new AudioSourceProcessSelection { Pid = value.Pid, Name = value.DisplayName }); }
            catch (Exception ex) { Log.Warning(ex, "Cannot select process"); }
        }
    }

    private async void OnPumpCrashed(object? sender, Exception ex)
    {
        Log.Error(ex, "Pipeline crashed; stopping");
        var speaker = _core.Selection.Speaker;
        try { await Pipeline.StopAsync(speaker); } catch { }
        try { _core.BeginStop(); _core.FinishStop(); } catch { }
        ErrorMessage = ex.Message;
        ShowNotification("Stream Error", ErrorMessage, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        StateLabel = "Idle";
    }

    partial void OnIsProcessSourceSelectedChanged(bool value)
    {
        StartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SourceStatusLabel));
        OnPropertyChanged(nameof(IsWholeSystemSourceSelected));
    }

    partial void OnShowAllAudioSessionsChanged(bool value)
    {
        AudioProcessStatus = value
            ? "Showing all non-system audio sessions. Some entries may not support per-app capture."
            : "Showing likely user-facing audio apps only.";
        _ = RefreshAudioProcessesAsync();
    }

    // Starts a background task that refreshes the AudioProcesses collection
    // every 2s so apps opening/closing their audio sessions appear in the
    // combo without requiring a manual Rescan. Must be called from the UI
    // thread so it can capture the DispatcherQueue.
    public void StartAudioProcessPolling()
    {
        if (_processPollTask != null) return;
        _dq = DispatcherQueue.GetForCurrentThread();
        _processPollCts = new CancellationTokenSource();
        var ct = _processPollCts.Token;
        _processPollTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var enumeration = ProcessLoopbackSource.EnumerateActiveAudioProcessesWithDiagnostics(ShowAllAudioSessions);
                    _dq?.TryEnqueue(() => SyncAudioProcessEnumeration(enumeration));
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Background audio process poll failed");
                }
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    private void SyncAudioProcessEnumeration(AudioProcessEnumerationResult enumeration)
    {
        LastAudioProcessEnumeration = enumeration;
        SyncAudioProcesses(enumeration.Processes);
        UpdateAudioProcessStatus(enumeration);
    }

    // Diffs the incoming list against AudioProcesses and adds/removes
    // individual items, instead of clearing + re-adding, so the ComboBox
    // selection isn't reset on every tick.
    private void SyncAudioProcesses(List<AudioProcess> incoming)
    {
        var incomingByPid = incoming.ToDictionary(p => p.Pid);
        for (int i = AudioProcesses.Count - 1; i >= 0; i--)
        {
            if (!incomingByPid.ContainsKey(AudioProcesses[i].Pid))
                AudioProcesses.RemoveAt(i);
        }
        var existing = AudioProcesses.Select(p => p.Pid).ToHashSet();
        foreach (var p in incoming)
        {
            if (!existing.Contains(p.Pid))
                AudioProcesses.Add(p);
        }

        if (IsProcessSourceSelected && SelectedProcess == null && !string.IsNullOrWhiteSpace(Settings.LastProcessName))
        {
            var previous = AudioProcesses.FirstOrDefault(p => string.Equals(p.Name, Settings.LastProcessName, StringComparison.OrdinalIgnoreCase));
            if (previous != null)
                SelectedProcess = previous;
        }

        StartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SourceStatusLabel));
    }

    private void UpdateAudioProcessStatus(AudioProcessEnumerationResult enumeration)
    {
        if (!string.IsNullOrWhiteSpace(enumeration.LastError))
        {
            AudioProcessStatus = $"Could not read app audio sessions: {enumeration.LastError}";
            return;
        }

        if (enumeration.TotalSessions == 0)
        {
            AudioProcessStatus = "No Windows audio sessions found. Start playback in the app, wait a few seconds, or use Whole system.";
            return;
        }

        if (enumeration.Kept == 0 && enumeration.FilteredSkipped > 0 && !enumeration.IncludeFilteredSessions)
        {
            AudioProcessStatus = $"Found {FormatCount(enumeration.TotalSessions, "audio session")}, but none matched the app filter. Try Refresh apps or enable Show all audio sessions.";
            return;
        }

        if (enumeration.Kept == 0)
        {
            AudioProcessStatus = $"Found {FormatCount(enumeration.TotalSessions, "audio session")}, but only system, expired, or RoomRelay sessions were available.";
            return;
        }

        var mode = enumeration.IncludeFilteredSessions ? " including advanced entries" : "";
        AudioProcessStatus = $"Found {FormatCount(enumeration.Kept, "app audio session")}{mode}.";
    }

    private static string FormatCount(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    [RelayCommand]
    public async Task RefreshAudioProcessesAsync()
    {
        try
        {
            var includeFiltered = ShowAllAudioSessions;
            var enumeration = await Task.Run(() => ProcessLoopbackSource.EnumerateActiveAudioProcessesWithDiagnostics(includeFiltered));
            SyncAudioProcessEnumeration(enumeration);
            Log.Information(
                "Audio process refresh: endpoints={Endpoints}, sessions={Sessions}, system={System}, filtered={Filtered}, self={Self}, expired={Expired}, kept={Kept}, includeFiltered={IncludeFiltered}",
                enumeration.EndpointsScanned,
                enumeration.TotalSessions,
                enumeration.SystemSkipped,
                enumeration.FilteredSkipped,
                enumeration.SelfSkipped,
                enumeration.ExpiredSkipped,
                enumeration.Kept,
                enumeration.IncludeFilteredSessions);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to refresh audio processes");
            AudioProcessStatus = $"Could not refresh app audio sessions: {ex.Message}";
        }
    }

    private void ApplyProcessPreference(string processName)
    {
        var pref = Settings.ProcessPreferences.FirstOrDefault(p => string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (pref == null) return;

        SelectedFormat = pref.StreamingFormat;
        SelectedLatencyMode = pref.LatencyMode;
    }

    private void RememberSelectedProcessPreference()
    {
        var processName = SelectedProcess?.Name;
        if (!IsProcessSourceSelected) return;
        if (string.IsNullOrWhiteSpace(processName)) return;

        var pref = Settings.ProcessPreferences.FirstOrDefault(p => string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (pref == null)
        {
            pref = new AppProcessPreference { ProcessName = processName };
            Settings.ProcessPreferences.Add(pref);
        }

        pref.StreamingFormat = SelectedFormat;
        pref.LatencyMode = SelectedLatencyMode;
    }

    [RelayCommand]
    public async Task RescanAsync()
    {
        var previousUdn = SelectedSpeaker?.Udn ?? _core.Selection.Speaker?.Udn ?? Settings.LastSpeakerUdn;
        List<SonosDevice> raw = new();
        try
        {
            raw = await _discovery.ScanAsync(3000);
            DiscoveryStatus = raw.Count == 0
                ? "No Sonos rooms found. Check that this PC and Sonos are on the same network, allow the firewall prompt, or add a room by IP."
                : $"Found {raw.Count} Sonos device{(raw.Count == 1 ? "" : "s")}. Resolving rooms...";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SSDP scan failed");
            DiscoveryStatus = "Discovery failed. Check network access or add a room by IP.";
        }

        var manual = await LookupSavedManualSpeakersAsync();
        var mergedRaw = SsdpDiscovery.MergeDevices(raw, manual);
        var resolved = await ResolveWithFallbackAsync(mergedRaw);
        Log.Information("Discovery scan: raw={RawCount}, manual={ManualCount}, merged={MergedCount}, resolved={ResolvedCount}",
            raw.Count, manual.Count, mergedRaw.Count, resolved.Count);
        SyncSpeakers(resolved);
        _core.SetDiscovered(resolved);
        Log.Information("Discovered {Count} speaker(s)", resolved.Count);
        DiscoveryStatus = resolved.Count == 0
            ? "No rooms available. Check that Sonos is powered on and reachable, then Rescan or Add by IP."
            : $"Found {resolved.Count} room{(resolved.Count == 1 ? "" : "s")}.";

        // Re-select by identity because the collection is rebuilt on every scan.
        var selected = false;
        if (!string.IsNullOrEmpty(previousUdn) && !IsNotStreaming)
        {
            var match = resolved.FirstOrDefault(d => string.Equals(d.Udn, previousUdn, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                PlaybackTargetLabel = match.FriendlyName;
        }

        if (!string.IsNullOrEmpty(previousUdn) && IsNotStreaming)
        {
            var match = resolved.FirstOrDefault(d => string.Equals(d.Udn, previousUdn, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                SelectedSpeaker = match;
                selected = true;
            }
        }

        if (!selected && IsNotStreaming)
            SelectedSpeaker = resolved.Count == 1 ? resolved[0] : null;

        try
        {
            var enumeration = ProcessLoopbackSource.EnumerateActiveAudioProcessesWithDiagnostics(ShowAllAudioSessions);
            SyncAudioProcessEnumeration(enumeration);
            Log.Information("Found {Count} active audio process(es)", enumeration.Kept);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate audio processes");
        }
    }

    public bool CanAddManualSpeaker => StateLabel == "Idle";
    public bool CanRemoveManualSpeaker => StateLabel == "Idle" && SelectedManualSpeakerEndpoint != null;

    [RelayCommand(CanExecute = nameof(CanAddManualSpeaker))]
    public async Task AddManualSpeakerAsync()
    {
        if (!TryParseManualEndpoint(ManualSpeakerIp, ManualSpeakerPort, out var ip, out var port, out var error))
        {
            ManualSpeakerStatus = error;
            return;
        }

        try
        {
            ManualSpeakerStatus = "Looking up speaker...";
            var direct = await _discovery.LookupAsync(ip, port);
            var resolved = await ResolveWithFallbackAsync([direct]);
            var merged = SsdpDiscovery.MergeDevices(Speakers, resolved);
            SyncSpeakers(merged);
            _core.SetDiscovered(merged);

            AddSavedManualEndpoint(ip, port);
            SyncSavedManualEndpoints();

            var selected = FindMatchingSpeaker(resolved.First(), merged) ?? resolved.First();
            SelectSpeaker(selected);

            ManualSpeakerIp = "";
            ManualSpeakerPort = "1400";
            ManualSpeakerStatus = $"Added {selected.FriendlyName}";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Manual speaker lookup failed for {Ip}:{Port}", ip, port);
            ManualSpeakerStatus = ex is InvalidOperationException && !string.IsNullOrWhiteSpace(ex.Message)
                ? ex.Message
                : $"Could not reach {ip}:{port}. Check the IP address, port 1400, firewall, VLAN, and that the device is a Sonos speaker.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveManualSpeaker))]
    public async Task RemoveManualSpeakerAsync()
    {
        var endpoint = SelectedManualSpeakerEndpoint;
        if (endpoint == null) return;

        var removed = Settings.ManualSpeakerEndpoints.RemoveAll(e =>
            e.Port == endpoint.Port &&
            string.Equals(e.Ip, endpoint.Ip, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
            return;

        Settings.Save();
        ManualSpeakerStatus = $"Removed {endpoint.DisplayName}";
        SyncSavedManualEndpoints();
        await RescanAsync();
    }

    [RelayCommand]
    public void SelectSpeaker(SonosDevice device)
    {
        try
        {
            _core.SetSpeaker(device);
            SelectedSpeaker = device;
            Settings.LastSpeakerUdn = device.Udn;
            Settings.Save();
        }
        catch (Exception ex) { Log.Warning(ex, "Cannot select speaker"); }
    }

    [RelayCommand]
    public void SelectSource(AudioSourceSelection selection)
    {
        IsProcessSourceSelected = selection == AudioSourceSelection.Process;
        try
        {
            var process = selection == AudioSourceSelection.Process && SelectedProcess != null
                ? new AudioSourceProcessSelection { Pid = SelectedProcess.Pid, Name = SelectedProcess.DisplayName }
                : null;
            _core.SetSource(selection, process);
        }
        catch (Exception ex) { Log.Warning(ex, "Cannot change source"); }
    }

    [RelayCommand]
    public void UseWholeSystemSource()
    {
        IsProcessSourceSelected = false;
        SelectSource(AudioSourceSelection.WholeSystem);
    }

    [RelayCommand]
    public void SelectProcess(AudioProcess process)
    {
        try { _core.SetSource(AudioSourceSelection.Process, new AudioSourceProcessSelection { Pid = process.Pid, Name = process.DisplayName }); }
        catch (Exception ex) { Log.Warning(ex, "Cannot select process"); }
    }

    public bool CanStart => StateLabel == "Idle" && SelectedSpeaker != null && (!IsProcessSourceSelected || SelectedProcess != null);
    public bool CanStop => StateLabel == "Streaming";

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.Brush StatusBrush { get; set; } = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);

    partial void OnStateLabelChanged(string value)
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        AddManualSpeakerCommand.NotifyCanExecuteChanged();
        RemoveManualSpeakerCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsNotStreaming));
        OnPropertyChanged(nameof(CanSelectLatencyMode));
        OnPropertyChanged(nameof(CanAddManualSpeaker));
        OnPropertyChanged(nameof(CanRemoveManualSpeaker));
        OnPropertyChanged(nameof(ClientStatusLabel));
        StatusBrush = value switch
        {
            "Streaming" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LimeGreen),
            "Idle" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
            _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
        };
    }

    private async Task<List<SonosDevice>> LookupSavedManualSpeakersAsync()
    {
        var devices = new List<SonosDevice>();
        foreach (var endpoint in Settings.ManualSpeakerEndpoints)
        {
            if (!IPAddress.TryParse(endpoint.Ip, out var ip))
            {
                Log.Warning("Skipping invalid saved manual speaker endpoint {Ip}:{Port}", endpoint.Ip, endpoint.Port);
                continue;
            }

            try
            {
                devices.Add(await _discovery.LookupAsync(ip, endpoint.Port));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Saved manual speaker lookup failed for {Ip}:{Port}", endpoint.Ip, endpoint.Port);
            }
        }

        return devices;
    }

    private async Task<List<SonosDevice>> ResolveWithFallbackAsync(List<SonosDevice> devices)
    {
        try
        {
            var resolved = await _topology.ResolveCoordinatorsAsync(devices);
            return resolved.Count == 0 && devices.Count > 0 ? devices : resolved;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Topology resolution failed; keeping directly discovered speaker(s)");
            return devices;
        }
    }

    private static bool TryParseManualEndpoint(
        string ipText,
        string portText,
        out IPAddress ip,
        out ushort port,
        out string error)
    {
        ip = IPAddress.None;
        port = 1400;
        error = "";

        if (!IPAddress.TryParse(ipText.Trim(), out ip!))
        {
            error = "Enter a valid IP address.";
            return false;
        }

        var trimmedPort = portText.Trim();
        if (string.IsNullOrEmpty(trimmedPort))
        {
            port = 1400;
            return true;
        }

        if (!ushort.TryParse(trimmedPort, out port) || port == 0)
        {
            error = "Enter a port from 1 to 65535.";
            return false;
        }

        return true;
    }

    private void SyncSpeakers(List<SonosDevice> devices)
    {
        Speakers.Clear();
        foreach (var d in devices) Speakers.Add(d);
    }

    private void AddSavedManualEndpoint(IPAddress ip, ushort port)
    {
        var ipText = ip.ToString();
        if (Settings.ManualSpeakerEndpoints.Any(e =>
                e.Port == port &&
                string.Equals(e.Ip, ipText, StringComparison.OrdinalIgnoreCase)))
            return;

        Settings.ManualSpeakerEndpoints.Add(new ManualSpeakerEndpoint { Ip = ipText, Port = port });
        Settings.Save();
    }

    private void SyncSavedManualEndpoints()
    {
        var previous = SelectedManualSpeakerEndpoint;
        SavedManualSpeakerEndpoints.Clear();
        foreach (var endpoint in Settings.ManualSpeakerEndpoints)
            SavedManualSpeakerEndpoints.Add(endpoint);

        SelectedManualSpeakerEndpoint = previous == null
            ? null
            : SavedManualSpeakerEndpoints.FirstOrDefault(e => e.Port == previous.Port && string.Equals(e.Ip, previous.Ip, StringComparison.OrdinalIgnoreCase));
        RemoveManualSpeakerCommand.NotifyCanExecuteChanged();
    }

    private static SonosDevice? FindMatchingSpeaker(SonosDevice device, IEnumerable<SonosDevice> candidates) =>
        candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Udn, device.Udn, StringComparison.OrdinalIgnoreCase) ||
            candidate.Ip.Equals(device.Ip) && candidate.Port == device.Port);

    partial void OnSelectedSpeakerChanged(SonosDevice? value)
    {
        StartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedSpeakerLabel));
        if (StateLabel != "Streaming")
            PlaybackTargetLabel = value?.FriendlyName ?? "No room selected";
        if (value != null)
        {
            try
            {
                _core.SetSpeaker(value);
                Settings.LastSpeakerUdn = value.Udn;
                Settings.Save();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Cannot select speaker; reverting");
                SelectedSpeaker = _core.Selection.Speaker;
                return;
            }
            _ = RefreshSonosVolumeAsync();
        }
        else
        {
            SonosVolumeStatus = "Select a room to read or set Sonos volume.";
        }
    }

    [RelayCommand]
    public async Task RefreshSonosVolumeAsync()
    {
        var speaker = SelectedSpeaker ?? _core.Selection.Speaker;
        if (speaker == null) return;

        try
        {
            var versionAtRefreshStart = Interlocked.Read(ref _sonosVolumeUserVersion);
            var volume = await new SonosController().GetVolumeAsync(speaker);
            if (versionAtRefreshStart != Interlocked.Read(ref _sonosVolumeUserVersion))
                return;

            _updatingSonosVolumeFromDevice = true;
            try { SonosVolume = volume; }
            finally { _updatingSonosVolumeFromDevice = false; }
            SonosVolumeStatus = $"Sonos volume: {volume}%";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read Sonos volume");
            SonosVolumeStatus = "Could not read Sonos volume";
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        var speaker = SelectedSpeaker;
        if (speaker == null) return;
        if (IsProcessSourceSelected && SelectedProcess == null)
        {
            ErrorMessage = "Select an application before starting per-application capture.";
            ShowNotification("Source Required", ErrorMessage, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
            StartCommand.NotifyCanExecuteChanged();
            return;
        }

        try
        {
            _core.BeginStart();
        }
        catch (InvalidOperationException ex)
        {
            Log.Debug(ex, "Start ignored (already running)");
            return;
        }

        try
        {
            IsErrorVisible = false;
            StateLabel = "Starting…";
            await Pipeline.StartAsync(speaker, CancellationToken.None);
            _core.FinishStart();
            StateLabel = "Streaming";
            PlaybackTargetLabel = speaker.FriendlyName;

            UpdateInputFormatLabel();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Start failed");
            try { await Pipeline.StopAsync(speaker); } catch { }
            try { _core.BeginStop(); _core.FinishStop(); } catch { }

            if (IsProcessSourceSelected && IsPerApplicationCaptureFailure(ex))
            {
                if (await TryStartWholeSystemFallbackAsync(speaker, ex))
                    return;
            }

            ShowNotification("Stream Error", ex.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            StateLabel = "Idle";
            OnPropertyChanged(nameof(SelectedSpeakerLabel));
        }
    }

    [RelayCommand]
    public void OpenLogsFolder()
    {
        try { _diagnostics.OpenLogsFolder(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open logs folder");
            ShowNotification("Logs Error", $"Could not open logs folder: {ex.Message}", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    public Task CreateDiagnosticsPackageAsync()
    {
        try
        {
            var package = _diagnostics.CreatePackage(
                _core,
                Pipeline,
                Settings,
                string.IsNullOrWhiteSpace(ErrorMessage) ? null : ErrorMessage,
                LastAudioProcessEnumeration);
            DiagnosticsStatus = $"Diagnostics package created: {package}";
            Log.Information("Diagnostics package created at {Path}", package);
            _diagnostics.OpenDiagnosticsPackage(package);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create diagnostics package");
            ShowNotification("Diagnostics Error", $"Could not create diagnostics package: {ex.Message}", Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
        }

        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    public async Task StopAsync()
    {
        try
        {
            _core.BeginStop();
            StateLabel = "Stopping…";
            var speaker = _core.Selection.Speaker;
            await Pipeline.StopAsync(speaker);
            _core.FinishStop();
            StateLabel = "Idle";
            PlaybackTargetLabel = SelectedSpeaker?.FriendlyName ?? "No room selected";
            InputFormatLabel = "—";
            ClientCount = 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Stop failed");
            try { _core.FinishStop(); } catch { }
            StateLabel = "Idle";
        }
    }

    private async Task<bool> TryStartWholeSystemFallbackAsync(SonosDevice speaker, Exception originalError)
    {
        var failedSource = SelectedProcess?.DisplayName ?? SelectedProcess?.Name ?? "the selected application";
        Log.Warning(originalError, "Per-application capture failed for {Source}; retrying with whole-system capture", failedSource);

        try
        {
            IsProcessSourceSelected = false;
            _core.SetSource(AudioSourceSelection.WholeSystem);
            _core.BeginStart();
            StateLabel = "Starting…";

            await Pipeline.StartAsync(speaker, CancellationToken.None);
            _core.FinishStart();
            StateLabel = "Streaming";
            PlaybackTargetLabel = speaker.FriendlyName;
            UpdateInputFormatLabel();

            ShowNotification(
                "Using Whole System",
                $"{failedSource} cannot be captured per application on this Windows build. RoomRelay switched to whole system audio.",
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
            return true;
        }
        catch (Exception fallbackEx)
        {
            Log.Error(fallbackEx, "Whole-system fallback failed");
            try { await Pipeline.StopAsync(speaker); } catch { }
            try { _core.BeginStop(); _core.FinishStop(); } catch { }

            ShowNotification(
                "Stream Error",
                $"Per-application capture failed, and whole-system fallback also failed: {fallbackEx.Message}",
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error);
            StateLabel = "Idle";
            OnPropertyChanged(nameof(SelectedSpeakerLabel));
            return true;
        }
    }

    private static bool IsPerApplicationCaptureFailure(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException!)
        {
            if (current is InvalidCastException)
                return true;

            var message = current.Message;
            if (message.Contains("Could not capture audio from", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("cannot be captured per-application", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("E_NOINTERFACE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("No such interface supported", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void UpdateInputFormatLabel()
    {
        if (Pipeline.CurrentMixFormat is MixFormat fmt)
            InputFormatLabel = $"{fmt.SampleRate} Hz · {fmt.Channels} ch · {fmt.BitsPerSample}-bit {(fmt.IsFloat ? "float" : "int")}";
    }

    private void ShowNotification(string title, string message, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity)
    {
        NotificationTitle = title;
        ErrorMessage = message;
        NotificationSeverity = severity;
        IsErrorVisible = true;
    }
}
