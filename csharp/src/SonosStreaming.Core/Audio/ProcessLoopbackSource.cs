using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using WinAudioClient = Windows.Win32.Media.Audio.IAudioClient;

namespace SonosStreaming.Core.Audio;

public sealed record AudioProcess(int Pid, string Name, string DisplayName);

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
    private readonly MixFormat _format = new(48000, 2, 32, true);

    public ProcessLoopbackSource(int pid, ProcessLoopbackMode mode = ProcessLoopbackMode.IncludeProcessTree)
    {
        _pid = (uint)pid;
        _mode = mode;
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

        const long hnsBufferDuration = 2_000_000L;
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
        Log.Information("Process loopback capture started for pid={Pid} mode={Mode}", _pid, _mode);
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

    public static unsafe List<AudioProcess> EnumerateActiveAudioProcesses()
    {
        var result = new List<AudioProcess>();
        int totalSessions = 0;
        int expiredSkipped = 0;
        int systemPidSkipped = 0;
        int endpointsScanned = 0;
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
                                            if (pid == 0) { systemPidSkipped++; continue; }

                                            AudioSessionState state;
                                            session.GetState(&state);
                                            if (state == AudioSessionState.AudioSessionStateExpired) { expiredSkipped++; continue; }
                                            if (pid == Environment.ProcessId) { continue; }

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
                                            if (!IsUserFacingProcess(pid, processName))
                                            {
                                                systemPidSkipped++;
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
            Log.Warning(ex, "Failed to enumerate audio sessions");
        }

        var deduped = result.DistinctBy(p => p.Pid).OrderBy(p => p.DisplayName).ToList();
        Log.Debug("Audio sessions: {Endpoints} endpoints, {Total} total sessions, {SysSkip} system, {ExpSkip} expired, {Kept} kept",
            endpointsScanned, totalSessions, systemPidSkipped, expiredSkipped, deduped.Count);
        return deduped;
    }

    private static readonly string[] SystemProcessNames = new[]
    {
        // Windows core
        "svchost", "csrss", "smss", "services", "lsass", "wininit", "winlogon",
        "dwm", "fontdrvhost", "conhost", "sihost", "taskhostw", "backgroundtaskhost",
        "runtimebroker", "dllhost", "wmiprvse", "searchindexer", "securityhealthservice",
        "ctfmon", "spoolsv", "audiodg", "musnotify", "wlanext", "vpnclient",
        "searchhost", "textinputhost", "shellexperiencehost", "startmenuexperiencehost",
        // Terminals / shells
        "windowsterminal", "wt", "cmd", "powershell", "pwsh",
        // IDEs / dev tools
        "devenv", "code",
        // Other background
        "onedrive", "teams", "outlook",
        // Browsers (loopback fails on most)
        "msedge", "chrome", "firefox", "opera", "brave", "vivaldi"
    };

    private static bool IsUserFacingProcess(int pid, string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        if (SystemProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)) return false;

        try
        {
            using var proc = Process.GetProcessById(pid);

            // If the executable lives under C:\Windows it's a system component.
            try
            {
                var path = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path))
                {
                    var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
            }
            catch { /* Access denied to MainModule for protected processes — fall through */ }

            // A user-facing app typically has a visible main window.
            if (proc.MainWindowHandle != IntPtr.Zero) return true;

            // Some legitimate media apps hide their main window when minimized to tray.
            if (!string.IsNullOrWhiteSpace(proc.MainWindowTitle)) return true;

            var knownMediaPlayers = new[] { "spotify", "foobar2000", "musicbee", "aimp", "vlc", "winamp", "mediamonkey", "groove" };
            if (knownMediaPlayers.Contains(processName, StringComparer.OrdinalIgnoreCase)) return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

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
