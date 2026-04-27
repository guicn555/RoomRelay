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

    [ObservableProperty]
    public partial SonosDevice? SelectedSpeaker { get; set; }

    public ObservableCollection<AudioProcess> AudioProcesses { get; } = new();

    [ObservableProperty]
    public partial string StateLabel { get; set; }

    [ObservableProperty]
    public partial string InputFormatLabel { get; set; }

    [ObservableProperty]
    public partial int ClientCount { get; set; }

    [ObservableProperty]
    public partial float VuL { get; set; }

    [ObservableProperty]
    public partial float VuR { get; set; }

    [ObservableProperty]
    public partial float[] Spectrum { get; set; }

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
    public partial bool IsProcessSourceSelected { get; set; }

    [ObservableProperty]
    public partial bool IsErrorVisible { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial StreamingFormat SelectedFormat { get; set; }

    [ObservableProperty]
    public partial string EncoderLabel { get; set; }

    [ObservableProperty]
    public partial string ManualSpeakerIp { get; set; }

    [ObservableProperty]
    public partial string ManualSpeakerPort { get; set; }

    [ObservableProperty]
    public partial string ManualSpeakerStatus { get; set; }

    [ObservableProperty]
    public partial string DiagnosticsStatus { get; set; }

    [ObservableProperty]
    public partial double SonosVolume { get; set; }

    [ObservableProperty]
    public partial string SonosVolumeStatus { get; set; }

    public string AppVersionLabel => $"RoomRelay {DiagnosticsPackageService.VersionLabel}";

    public string SelectedSpeakerLabel => SelectedSpeaker?.FriendlyName
        ?? _core.Selection.Speaker?.FriendlyName
        ?? "No room selected";

    public StreamingFormat[] AvailableFormats { get; } = Enum.GetValues<StreamingFormat>();
    public string[] AvailableFormatNames { get; } = Enum.GetValues<StreamingFormat>().Select(f => f.DisplayName()).ToArray();

    public int SelectedFormatIndex
    {
        get => (int)SelectedFormat;
        set { if (value >= 0) SelectedFormat = (StreamingFormat)value; }
    }

    public bool IsNotStreaming => StateLabel != "Streaming";

    private readonly ISsdpDiscovery _discovery;
    private readonly ITopologyResolver _topology;
    private DispatcherQueue? _dq;
    private Task? _processPollTask;
    private CancellationTokenSource? _processPollCts;
    private CancellationTokenSource? _sonosVolumeDebounceCts;
    private bool _allowSonosVolumeApply;
    private bool _updatingSonosVolumeFromDevice;

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
        ErrorMessage = "";
        SelectedFormat = settings.StreamingFormat;
        EncoderLabel = settings.StreamingFormat.DisplayName();
        ManualSpeakerIp = "";
        ManualSpeakerPort = "1400";
        ManualSpeakerStatus = "";
        DiagnosticsStatus = "";
        SonosVolume = 0;
        SonosVolumeStatus = "";
        Pipeline.Format = settings.StreamingFormat;

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
        IsErrorVisible = true;
        StateLabel = "Error: format changed";
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
        Pipeline.Format = value;
        Settings.StreamingFormat = value;
        Settings.Save();
        EncoderLabel = value.DisplayName();
        OnPropertyChanged(nameof(SelectedFormatIndex));
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

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, token).ConfigureAwait(false);
                await new SonosController().SetVolumeAsync(speaker, volume, token).ConfigureAwait(false);
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
        if (value != null)
        {
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
        IsErrorVisible = true;
        StateLabel = $"Error: {ex.Message}";
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
                    var procs = ProcessLoopbackSource.EnumerateActiveAudioProcesses();
                    _dq?.TryEnqueue(() => SyncAudioProcesses(procs));
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Background audio process poll failed");
                }
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            }
        }, ct);
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
    }

    [RelayCommand]
    public async Task RescanAsync()
    {
        var previousUdn = SelectedSpeaker?.Udn ?? _core.Selection.Speaker?.Udn ?? Settings.LastSpeakerUdn;
        List<SonosDevice> raw = new();
        try
        {
            raw = await _discovery.ScanAsync(3000);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SSDP scan failed");
        }

        var manual = await LookupSavedManualSpeakersAsync();
        var mergedRaw = SsdpDiscovery.MergeDevices(raw, manual);
        var resolved = await ResolveWithFallbackAsync(mergedRaw);
        Log.Information("Discovery scan: raw={RawCount}, manual={ManualCount}, merged={MergedCount}, resolved={ResolvedCount}",
            raw.Count, manual.Count, mergedRaw.Count, resolved.Count);
        SyncSpeakers(resolved);
        _core.SetDiscovered(resolved);
        Log.Information("Discovered {Count} speaker(s)", resolved.Count);

        // Re-select by identity because the collection is rebuilt on every scan.
        if (!string.IsNullOrEmpty(previousUdn) && IsNotStreaming)
        {
            var match = resolved.FirstOrDefault(d => string.Equals(d.Udn, previousUdn, StringComparison.OrdinalIgnoreCase));
            if (match != null) SelectedSpeaker = match;
        }

        try
        {
            var procs = ProcessLoopbackSource.EnumerateActiveAudioProcesses();
            SyncAudioProcesses(procs);
            Log.Information("Found {Count} active audio process(es)", procs.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to enumerate audio processes");
        }
    }

    public bool CanAddManualSpeaker => StateLabel == "Idle";

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

            var selected = FindMatchingSpeaker(resolved.First(), merged) ?? resolved.First();
            SelectSpeaker(selected);

            ManualSpeakerIp = "";
            ManualSpeakerPort = "1400";
            ManualSpeakerStatus = $"Added {selected.FriendlyName}";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Manual speaker lookup failed for {Ip}:{Port}", ip, port);
            ManualSpeakerStatus = $"Could not reach {ip}:{port}";
        }
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
        try { _core.SetSource(selection); }
        catch (Exception ex) { Log.Warning(ex, "Cannot change source"); }
    }

    [RelayCommand]
    public void SelectProcess(AudioProcess process)
    {
        try { _core.SetSource(AudioSourceSelection.Process, new AudioSourceProcessSelection { Pid = process.Pid, Name = process.DisplayName }); }
        catch (Exception ex) { Log.Warning(ex, "Cannot select process"); }
    }

    public bool CanStart => StateLabel == "Idle" && SelectedSpeaker != null;
    public bool CanStop => StateLabel == "Streaming";

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Media.Brush StatusBrush { get; set; } = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);

    partial void OnStateLabelChanged(string value)
    {
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        AddManualSpeakerCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsNotStreaming));
        OnPropertyChanged(nameof(CanAddManualSpeaker));
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

    private static SonosDevice? FindMatchingSpeaker(SonosDevice device, IEnumerable<SonosDevice> candidates) =>
        candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Udn, device.Udn, StringComparison.OrdinalIgnoreCase) ||
            candidate.Ip.Equals(device.Ip) && candidate.Port == device.Port);

    partial void OnSelectedSpeakerChanged(SonosDevice? value)
    {
        StartCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedSpeakerLabel));
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
    }

    [RelayCommand]
    public async Task RefreshSonosVolumeAsync()
    {
        var speaker = SelectedSpeaker ?? _core.Selection.Speaker;
        if (speaker == null) return;

        try
        {
            var volume = await new SonosController().GetVolumeAsync(speaker);
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
        var speaker = _core.Selection.Speaker;
        if (speaker == null) return;

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

            if (Pipeline.CurrentMixFormat is MixFormat fmt)
                InputFormatLabel = $"{fmt.SampleRate} Hz · {fmt.Channels} ch · {fmt.BitsPerSample}-bit {(fmt.IsFloat ? "float" : "int")}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Start failed");
            try { await Pipeline.StopAsync(speaker); } catch { }
            try { _core.BeginStop(); _core.FinishStop(); } catch { }
            ErrorMessage = ex.Message;
            IsErrorVisible = true;
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
            ErrorMessage = $"Could not open logs folder: {ex.Message}";
            IsErrorVisible = true;
        }
    }

    [RelayCommand]
    public Task CreateDiagnosticsPackageAsync()
    {
        try
        {
            var package = _diagnostics.CreatePackage(_core, Pipeline, string.IsNullOrWhiteSpace(ErrorMessage) ? null : ErrorMessage);
            DiagnosticsStatus = $"Diagnostics package created: {package}";
            Log.Information("Diagnostics package created at {Path}", package);
            _diagnostics.OpenDiagnosticsPackage(package);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create diagnostics package");
            ErrorMessage = $"Could not create diagnostics package: {ex.Message}";
            IsErrorVisible = true;
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
}
