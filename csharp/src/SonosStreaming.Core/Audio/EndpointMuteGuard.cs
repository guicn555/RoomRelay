using System.Runtime.InteropServices;
using Serilog;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;

namespace SonosStreaming.Core.Audio;

// Mutes the default render endpoint on construction (so the room stays
// quiet while Sonos plays the streamed audio — WASAPI loopback captures
// post-mix but pre-mute, so the broadcast still carries real audio) and
// restores the previous mute state when disposed.
public sealed unsafe class EndpointMuteGuard : IDisposable
{
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private const uint CLSCTX_INPROC_SERVER = 0x1;

    [DllImport("Ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    private readonly bool _previousMute;
    private bool _restored;

    public EndpointMuteGuard()
    {
        var volume = GetDefaultRenderEndpointVolume();
        try
        {
            BOOL wasMuted;
            volume.GetMute(&wasMuted);
            _previousMute = wasMuted;
            if (!_previousMute)
            {
                volume.SetMute(true, null);
                Log.Information("Muted default render endpoint");
            }
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
                volume.SetMute(_previousMute, null);
                Log.Information("Restored default render endpoint mute state to {Muted}", _previousMute);
            }
            finally { Marshal.ReleaseComObject(volume); }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore endpoint mute state");
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
