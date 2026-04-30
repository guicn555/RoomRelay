using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using WinAudioClient = Windows.Win32.Media.Audio.IAudioClient;

namespace SonosStreaming.Core.Audio;

public sealed record AudioProcess(int Pid, string Name, string DisplayName);

public sealed record AudioProcessEnumerationResult
{
    public List<AudioProcess> Processes { get; init; } = new();
    public int EndpointsScanned { get; init; }
    public int TotalSessions { get; init; }
    public int ExpiredSkipped { get; init; }
    public int SelfSkipped { get; init; }
    public int SystemSkipped { get; init; }
    public int FilteredSkipped { get; init; }
    public bool IncludeFilteredSessions { get; init; }
    public string? LastError { get; init; }
    public int Kept => Processes.Count;
}

public enum ProcessLoopbackMode
{
    IncludeProcessTree,
    ExcludeProcessTree,
}

// Captures audio played by a specific process using the Windows 10 21H2+
// process loopback API (ActivateAudioInterfaceAsync with VAD\Process_Loopback).
public sealed unsafe class ProcessLoopbackSource : WasapiCaptureBase
{
    private const int VT_BLOB = 65;
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        in Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    private readonly uint _pid;
    private readonly ProcessLoopbackMode _mode;
    private readonly int _captureBufferMs;
    private readonly MixFormat _format = new(48000, 2, 32, true);

    public ProcessLoopbackSource(int pid, ProcessLoopbackMode mode = ProcessLoopbackMode.IncludeProcessTree, int captureBufferMs = 200)
    {
        _pid = (uint)pid;
        _mode = mode;
        _captureBufferMs = captureBufferMs;
    }

    public new MixFormat MixFormat => _format;

    public static bool IsSupported(out string reason)
    {
        var version = Environment.OSVersion.Version;
        if (!OperatingSystem.IsWindows())
        {
            reason = "Per-application capture is only available on Windows.";
            return false;
        }

        if (version.Major < 10 || version.Build < 19044)
        {
            reason = $"Per-application capture requires Windows 10 21H2/build 19044 or newer. This PC reports build {version.Build}.";
            return false;
        }

        reason = "Per-application capture is available, but some protected, elevated, or short-lived app sessions may not be capturable.";
        return true;
    }

    public void Start()
    {
        var client = ActivateSync();

        var wfx = new WAVEFORMATEX
        {
            wFormatTag = 0x0003, // WAVE_FORMAT_IEEE_FLOAT
            nChannels = 2,
            nSamplesPerSec = 48000,
            wBitsPerSample = 32,
            nBlockAlign = 2 * 32 / 8,
            nAvgBytesPerSec = 48000 * 2 * 32 / 8,
            cbSize = 0,
        };

        long hnsBufferDuration = _captureBufferMs * 10_000L;
        try
        {
            client.Initialize(
                AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                hnsBufferDuration,
                0,
                &wfx,
                null);
        }
        catch (InvalidCastException ice) when (ice.Message.Contains("IAudioClient"))
        {
            throw new InvalidOperationException(
                $"This application (pid={_pid}) cannot be captured per-application. "
                + "Browsers, system apps, and protected/elevated processes are not supported by Windows process loopback. "
                + "Switch to 'Whole system' capture mode instead.", ice);
        }

        BeginCapture(client, &wfx, $"ProcessLoopback-{_pid}");
        Log.Information("Process loopback capture started for pid={Pid} mode={Mode} buffer={BufferMs} ms", _pid, _mode, _captureBufferMs);
    }

    private WinAudioClient ActivateSync()
    {
        IntPtr paramsPtr = Marshal.AllocCoTaskMem(12);
        Marshal.WriteInt32(paramsPtr, 0, (int)AUDIOCLIENT_ACTIVATION_TYPE.AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK);
        Marshal.WriteInt32(paramsPtr, 4, (int)_pid);
        Marshal.WriteInt32(paramsPtr, 8, _mode == ProcessLoopbackMode.IncludeProcessTree ? 0 : 1);

        IntPtr pvPtr = Marshal.AllocCoTaskMem(24);
        for (int i = 0; i < 24; i++) Marshal.WriteByte(pvPtr, i, 0);
        Marshal.WriteInt16(pvPtr, 0, VT_BLOB);
        Marshal.WriteInt32(pvPtr, 8, 12);
        Marshal.WriteIntPtr(pvPtr, 16, paramsPtr);

        using var done = new ManualResetEventSlim(false);
        WinAudioClient? result = null;
        int resultHr = 0;

        var handler = new ActivationHandler((op) =>
        {
            try
            {
                unsafe
                {
                    HRESULT hr;
                    op.GetActivateResult(&hr, out var iface);
                    resultHr = hr.Value;
                    if (iface == null) return;

                    var pUnk = Marshal.GetIUnknownForObject(iface);
                    try
                    {
                        var iid = IID_IAudioClient;
                        int qi = Marshal.QueryInterface(pUnk, in iid, out var pAudioClient);
                        if (qi >= 0 && pAudioClient != IntPtr.Zero)
                        {
                            try
                            {
                                result = (WinAudioClient)Marshal.GetObjectForIUnknown(pAudioClient);
                            }
                            catch (InvalidCastException ice)
                            {
                                resultHr = unchecked((int)0x80004002); // E_NOINTERFACE
                                Log.Warning(ice, "Process loopback cast to IAudioClient failed for pid={Pid}. " +
                                    "The process ({Name}) may be a protected/system process. " +
                                    "Try 'Whole system' mode instead.", _pid, _pid);
                            }
                            Marshal.Release(pAudioClient);
                        }
                        else if (qi < 0)
                        {
                            resultHr = qi;
                            Log.Warning("Process loopback QueryInterface failed for pid={Pid}: 0x{Hr:X8}. " +
                                "The process may not have an active audio session, or it may be protected/elevated. " +
                                "Try 'Whole system' mode instead.", _pid, qi);
                        }
                    }
                    finally { Marshal.Release(pUnk); }
                }
            }
            finally
            {
                done.Set();
            }
        });

        try
        {
            int callHr = ActivateAudioInterfaceAsync(
                "VAD\\Process_Loopback",
                in IID_IAudioClient,
                pvPtr,
                handler,
                out _);
            if (callHr < 0)
                throw Marshal.GetExceptionForHR(callHr) ?? new Exception($"ActivateAudioInterfaceAsync failed: 0x{callHr:X8}");

            if (!done.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Process loopback activation timed out after 10 seconds.");
            if (resultHr < 0)
            {
                var msg = $"Process loopback activation failed for pid={_pid}: 0x{resultHr:X8}. "
                    + "The application may not have an active audio session, or it may be protected/elevated. "
                    + "Try 'Whole system' capture mode instead.";
                Log.Warning(msg);
                throw new InvalidOperationException(msg);
            }
            if (result == null)
                throw new InvalidOperationException("Process loopback activation produced no IAudioClient");

            return result;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pvPtr);
            Marshal.FreeCoTaskMem(paramsPtr);
        }
    }

    protected override unsafe void ConvertToFloat(byte* src, float[] dst, uint frames)
    {
        int totalSamples = (int)frames * 2;
        fixed (float* d = dst)
        {
            Buffer.MemoryCopy(src, d, totalSamples * 4, totalSamples * 4);
        }
    }

    protected override void OnDispose()
    {
        Log.Information("Process loopback capture stopped for pid={Pid}", _pid);
    }

    [ComVisible(true)]
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly Action<IActivateAudioInterfaceAsyncOperation> _callback;
        public ActivationHandler(Action<IActivateAudioInterfaceAsyncOperation> callback) => _callback = callback;
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => _callback(activateOperation);
    }

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private const uint DEVICE_STATE_ACTIVE = 0x1;

    [DllImport("Ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    public static List<AudioProcess> EnumerateActiveAudioProcesses() =>
        EnumerateActiveAudioProcessesWithDiagnostics(includeFilteredSessions: false).Processes;

    public static unsafe AudioProcessEnumerationResult EnumerateActiveAudioProcessesWithDiagnostics(bool includeFilteredSessions = false)
    {
        var result = new List<AudioProcess>();
        int totalSessions = 0;
        int expiredSkipped = 0;
        int selfSkipped = 0;
        int systemSkipped = 0;
        int filteredSkipped = 0;
        int endpointsScanned = 0;
        string? lastError = null;
        try
        {
            int hr = CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, 1u, IID_IMMDeviceEnumerator, out var pEnum);
            if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
            var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pEnum);
            Marshal.Release(pEnum);

            try
            {
                enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE.DEVICE_STATE_ACTIVE, out var devices);
                try
                {
                    devices.GetCount(out var deviceCount);
                    endpointsScanned = (int)deviceCount;
                    for (uint d = 0; d < deviceCount; d++)
                    {
                        devices.Item(d, out var device);
                        try
                        {
                            var iidMgr = IID_IAudioSessionManager2;
                            device.Activate(&iidMgr, Windows.Win32.System.Com.CLSCTX.CLSCTX_INPROC_SERVER, null, out var mgrObj);
                            var sessionManager = (IAudioSessionManager2)mgrObj;
                            try
                            {
                                var sessionEnum = sessionManager.GetSessionEnumerator();
                                try
                                {
                                    sessionEnum.GetCount(out var sessionCount);
                                    totalSessions += sessionCount;
                                    for (int i = 0; i < sessionCount; i++)
                                    {
                                        sessionEnum.GetSession(i, out var session);
                                        try
                                        {
                                            var control2 = (IAudioSessionControl2)session;
                                            control2.GetProcessId(out var pidU);
                                            int pid = (int)pidU;
                                            if (pid == 0) { systemSkipped++; continue; }

                                            AudioSessionState state;
                                            session.GetState(&state);
                                            if (state == AudioSessionState.AudioSessionStateExpired) { expiredSkipped++; continue; }
                                            if (pid == Environment.ProcessId) { selfSkipped++; continue; }

                                            string? sessionName = null;
                                            try
                                            {
                                                PWSTR pName;
                                                session.GetDisplayName(&pName);
                                                if (pName.Value != null)
                                                {
                                                    sessionName = new string(pName);
                                                    Marshal.FreeCoTaskMem((IntPtr)pName.Value);
                                                }
                                            }
                                            catch { }

                                            var (processName, friendlyName) = GetProcessLabels(pid);
                                            if (IsHardSystemProcess(pid, processName))
                                            {
                                                systemSkipped++;
                                                Log.Verbose("Skipping system audio session pid={Pid} process={Process} session={Session} friendly={Friendly}",
                                                    pid, processName ?? "n/a", sessionName ?? "n/a", friendlyName ?? "n/a");
                                                continue;
                                            }

                                            if (!includeFilteredSessions && !IsUserFacingProcess(pid, processName, sessionName, friendlyName))
                                            {
                                                filteredSkipped++;
                                                Log.Verbose("Filtering audio session pid={Pid} process={Process} session={Session} friendly={Friendly}",
                                                    pid, processName ?? "n/a", sessionName ?? "n/a", friendlyName ?? "n/a");
                                                continue;
                                            }

                                            string displayName = !string.IsNullOrEmpty(sessionName)
                                                ? sessionName
                                                : (friendlyName ?? processName ?? $"pid {pid}");

                                            result.Add(new AudioProcess(pid, processName ?? $"pid {pid}", displayName));
                                        }
                                        finally { Marshal.ReleaseComObject(session); }
                                    }
                                }
                                finally { Marshal.ReleaseComObject(sessionEnum); }
                            }
                            finally { Marshal.ReleaseComObject(sessionManager); }
                        }
                        finally { Marshal.ReleaseComObject(device); }
                    }
                }
                finally { Marshal.ReleaseComObject(devices); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            Log.Warning(ex, "Failed to enumerate audio sessions");
        }

        var deduped = result.DistinctBy(p => p.Pid).OrderBy(p => p.DisplayName).ToList();
        Log.Debug("Audio sessions: {Endpoints} endpoints, {Total} total sessions, {SysSkip} system, {FilteredSkip} filtered, {SelfSkip} self, {ExpSkip} expired, {Kept} kept, includeFiltered={IncludeFiltered}",
            endpointsScanned, totalSessions, systemSkipped, filteredSkipped, selfSkipped, expiredSkipped, deduped.Count, includeFilteredSessions);
        return new AudioProcessEnumerationResult
        {
            Processes = deduped,
            EndpointsScanned = endpointsScanned,
            TotalSessions = totalSessions,
            ExpiredSkipped = expiredSkipped,
            SelfSkipped = selfSkipped,
            SystemSkipped = systemSkipped,
            FilteredSkipped = filteredSkipped,
            IncludeFilteredSessions = includeFilteredSessions,
            LastError = lastError,
        };
    }

    private static readonly string[] HardSystemProcessNames = new[]
    {
        // Windows core
        "svchost", "csrss", "smss", "services", "lsass", "wininit", "winlogon",
        "dwm", "fontdrvhost", "conhost", "sihost", "taskhostw", "backgroundtaskhost",
        "runtimebroker", "dllhost", "wmiprvse", "searchindexer", "securityhealthservice",
        "ctfmon", "spoolsv", "audiodg", "musnotify", "wlanext", "vpnclient",
        "searchhost", "textinputhost", "shellexperiencehost", "startmenuexperiencehost"
    };

    private static readonly string[] DefaultHiddenProcessNames = new[]
    {
        // Terminals / shells
        "windowsterminal", "wt", "cmd", "powershell", "pwsh",
        // IDEs / dev tools
        "devenv", "code",
        // Other background
        "onedrive", "teams", "outlook",
        // Browsers (loopback fails on most)
        "msedge", "chrome", "firefox", "opera", "brave", "vivaldi"
    };

    private static readonly string[] KnownMediaPlayers = new[]
    {
        "spotify", "foobar2000", "musicbee", "aimp", "vlc", "winamp", "mediamonkey", "groove",
        "wmplayer", "mediaplayer", "microsoft.media.player", "zunemusic",
        "applemusic", "itunes", "tidal", "qobuz", "deezer", "amazonmusic", "amazon music",
        "plexamp", "roon", "audirvana", "jriver", "musicbee", "potplayer", "mpc-hc", "mpc-be",
        "youtube music", "ytmusic", "youtubemusic"
    };

    private static readonly string[] KnownMediaLabels = new[]
    {
        "media player", "windows media player", "spotify", "foobar2000", "musicbee", "aimp", "vlc", "winamp", "mediamonkey",
        "apple music", "itunes", "tidal", "qobuz", "deezer", "amazon music", "plexamp", "roon", "audirvana",
        "youtube music", "yt music", "jriver", "potplayer", "media player classic", "mpc-hc", "mpc-be"
    };

    private static bool IsUserFacingProcess(int pid, string? processName, string? sessionName, string? friendlyName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        if (IsKnownMediaProcess(processName) || IsKnownMediaLabel(sessionName) || IsKnownMediaLabel(friendlyName)) return true;
        if (DefaultHiddenProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)) return false;

        try
        {
            using var proc = Process.GetProcessById(pid);

            // A user-facing app typically has a visible main window.
            if (proc.MainWindowHandle != IntPtr.Zero) return true;

            // Some legitimate media apps hide their main window when minimized to tray.
            if (!string.IsNullOrWhiteSpace(proc.MainWindowTitle)) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsHardSystemProcess(int pid, string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        if (HardSystemProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)) return true;

        try
        {
            using var proc = Process.GetProcessById(pid);
            var path = proc.MainModule?.FileName;
            if (string.IsNullOrEmpty(path)) return false;

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsKnownMediaProcess(string processName) =>
        KnownMediaPlayers.Contains(processName, StringComparer.OrdinalIgnoreCase);

    private static bool IsKnownMediaLabel(string? label) =>
        !string.IsNullOrWhiteSpace(label) &&
        KnownMediaLabels.Any(known => label.Contains(known, StringComparison.OrdinalIgnoreCase));

    private static (string? processName, string? friendlyName) GetProcessLabels(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            string? friendly = null;
            try
            {
                var fileName = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(fileName))
                {
                    var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(fileName);
                    friendly = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription
                             : !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName
                             : null;
                }
            }
            catch { }
            return (proc.ProcessName, friendly);
        }
        catch
        {
            return (null, null);
        }
    }
}
