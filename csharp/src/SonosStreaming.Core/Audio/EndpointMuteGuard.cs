using System.Runtime.InteropServices;
using Serilog;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;

namespace SonosStreaming.Core.Audio;

// Controls the default render endpoint's mute + master volume so the room can
// be silenced while Sonos plays the streamed audio. Captures the original
// mute/volume on construction (changing nothing) and restores them on dispose.
//
// NOTE (issue #23): this build does NOT silence on construction. Whether the
// endpoint mute (or a zeroed master volume) sits before or after the WASAPI
// loopback tap is driver-dependent, so PipelineRunner drives a short A/B/C
// probe (audible -> mute -> volume-0) and picks whichever silences the room
// without also silencing the loopback that feeds Sonos.
public sealed unsafe class EndpointMuteGuard : IDisposable
{
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private const uint CLSCTX_INPROC_SERVER = 0x1;

    [DllImport("Ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    private readonly bool _previousMute;
    private readonly float _previousVolume;
    private bool _restored;

    public EndpointMuteGuard()
    {
        var volume = GetDefaultRenderEndpointVolume();
        try
        {
            BOOL wasMuted;
            volume.GetMute(&wasMuted);
            _previousMute = wasMuted;

            volume.GetMasterVolumeLevelScalar(out float level);
            _previousVolume = level;
        }
        finally { Marshal.ReleaseComObject(volume); }
    }

    public bool PreviousMute => _previousMute;
    public float PreviousVolume => _previousVolume;

    // Probe phase A: leave the endpoint audible at its original level.
    public void ApplyAudible()
    {
        if (_restored) return;
        var volume = GetDefaultRenderEndpointVolume();
        try
        {
            volume.SetMute(false, null);
            volume.SetMasterVolumeLevelScalar(_previousVolume, null);
            Log.Information("Probe: endpoint left audible (volume {Vol:P0})", _previousVolume);
        }
        finally { Marshal.ReleaseComObject(volume); }
    }

    // Probe phase B / strategy: mute the endpoint (current shipping behavior).
    public void ApplyMute()
    {
        if (_restored) return;
        var volume = GetDefaultRenderEndpointVolume();
        try
        {
            volume.SetMute(true, null);
            Log.Information("Probe: applied endpoint MUTE");
        }
        finally { Marshal.ReleaseComObject(volume); }
    }

    // Probe phase C / strategy: unmute and drop master volume to zero.
    public void ApplyVolumeZero()
    {
        if (_restored) return;
        var volume = GetDefaultRenderEndpointVolume();
        try
        {
            volume.SetMute(false, null);
            volume.SetMasterVolumeLevelScalar(0f, null);
            Log.Information("Probe: applied endpoint VOLUME=0 (unmuted)");
        }
        finally { Marshal.ReleaseComObject(volume); }
    }

    public void Restore()
    {
        if (_restored) return;
        _restored = true;
        try
        {
            var volume = GetDefaultRenderEndpointVolume();
            try
            {
                volume.SetMasterVolumeLevelScalar(_previousVolume, null);
                volume.SetMute(_previousMute, null);
                Log.Information("Restored default render endpoint to mute={Muted}, volume={Vol:P0}", _previousMute, _previousVolume);
            }
            finally { Marshal.ReleaseComObject(volume); }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore endpoint mute/volume state");
        }
    }

    public void Dispose() => Restore();

    // Returns IAudioEndpointVolume for the default render / multimedia endpoint.
    // Caller owns the returned RCW and must ReleaseComObject it.
    private static IAudioEndpointVolume GetDefaultRenderEndpointVolume()
    {
        int hr = CoCreateInstance(CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_IMMDeviceEnumerator, out var pEnum);
        if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
        var enumerator = (IMMDeviceEnumerator)Marshal.GetObjectForIUnknown(pEnum);
        Marshal.Release(pEnum);
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            try
            {
                var iid = IID_IAudioEndpointVolume;
                device.Activate(&iid, CLSCTX.CLSCTX_INPROC_SERVER, null, out var pVol);
                return (IAudioEndpointVolume)pVol;
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }
}
