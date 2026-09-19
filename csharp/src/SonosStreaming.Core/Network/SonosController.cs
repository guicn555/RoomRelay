using System.Net;
using System.Text;
using Serilog;

namespace SonosStreaming.Core.Network;

public sealed class SonosController : ISonosController
{
    private const string SoapNsAvTransport = "urn:schemas-upnp-org:service:AVTransport:1";
    private const string SoapNsRenderingControl = "urn:schemas-upnp-org:service:RenderingControl:1";
    private const string SoapNsGroupRenderingControl = "urn:schemas-upnp-org:service:GroupRenderingControl:1";
    private readonly HttpClient _http;

    // GroupRenderingControl is only useful on a zone-group coordinator, and it
    // is not advertised in device_description.xml, so failures are expected on
    // bonded satellites (a Sub answers HTTP 500). Remember which endpoints
    // rejected it so every slider move doesn't pay for a failing round trip.
    private static readonly HashSet<string> NoGroupRenderingControl = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock NoGroupLock = new();
    public SonosController()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public static string BuildSetUriEnvelope(
        string streamUrl,
        bool useRadioScheme = true,
        string? metadataTitle = null,
        string? metadataResourceUrl = null,
        string contentType = "audio/aac")
    {
        var escaped = XmlEscape(streamUrl);
        // x-rincon-mp3radio:// forces Sonos into its MPEG radio decoder.
        // For LPCM (audio/L16) we send the plain http:// URI so Sonos
        // respects the Content-Type header and picks the PCM decoder.
        var currentUri = useRadioScheme ? $"x-rincon-mp3radio://{escaped}" : escaped;

        var metadata = "";
        if (!string.IsNullOrEmpty(metadataTitle))
        {
            var safeTitle = XmlEscape(metadataTitle);
            var safeResource = !string.IsNullOrWhiteSpace(metadataResourceUrl) ? XmlEscape(metadataResourceUrl) : null;
            var safeProtocolInfo = XmlEscape($"http-get:*:{contentType}:*");
            var res = safeResource == null ? "" : $"<res protocolInfo=\"{safeProtocolInfo}\">{safeResource}</res>";
            var didl = $"<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" " +
                        $"xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
                        $"xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">" +
                        $"<item id=\"-1\" parentID=\"-1\" restricted=\"true\">" +
                        $"<dc:title>{safeTitle}</dc:title>" +
                        $"<upnp:class>object.item.audioItem.audioBroadcast</upnp:class>" +
                        res +
                        $"</item></DIDL-Lite>";
            metadata = XmlEscape(didl);
        }

        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
               "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
               " <s:Body>\r\n" +
               $"  <u:SetAVTransportURI xmlns:u=\"{SoapNsAvTransport}\">\r\n" +
               "   <InstanceID>0</InstanceID>\r\n" +
               $"   <CurrentURI>{currentUri}</CurrentURI>\r\n" +
               $"   <CurrentURIMetaData>{metadata}</CurrentURIMetaData>\r\n" +
               "  </u:SetAVTransportURI>\r\n" +
               " </s:Body>\r\n" +
               "</s:Envelope>";
    }

    public static string BuildPlayEnvelope()
    {
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
               "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
               " <s:Body>\r\n" +
               $"  <u:Play xmlns:u=\"{SoapNsAvTransport}\">\r\n" +
               "   <InstanceID>0</InstanceID>\r\n" +
               "   <Speed>1</Speed>\r\n" +
               "  </u:Play>\r\n" +
               " </s:Body>\r\n" +
               "</s:Envelope>";
    }

    public static string BuildStopEnvelope()
    {
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
               "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
               " <s:Body>\r\n" +
               $"  <u:Stop xmlns:u=\"{SoapNsAvTransport}\">\r\n" +
               "   <InstanceID>0</InstanceID>\r\n" +
               "  </u:Stop>\r\n" +
               " </s:Body>\r\n" +
               "</s:Envelope>";
    }

    public static string BuildGetVolumeEnvelope()
    {
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
               "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
               " <s:Body>\r\n" +
               $"  <u:GetVolume xmlns:u=\"{SoapNsRenderingControl}\">\r\n" +
               "   <InstanceID>0</InstanceID>\r\n" +
               "   <Channel>Master</Channel>\r\n" +
               "  </u:GetVolume>\r\n" +
               " </s:Body>\r\n" +
               "</s:Envelope>";
    }

    public static string BuildSetVolumeEnvelope(int volume)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
               "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
               " <s:Body>\r\n" +
               $"  <u:SetVolume xmlns:u=\"{SoapNsRenderingControl}\">\r\n" +
               "   <InstanceID>0</InstanceID>\r\n" +
               "   <Channel>Master</Channel>\r\n" +
               $"   <DesiredVolume>{clamped}</DesiredVolume>\r\n" +
               "  </u:SetVolume>\r\n" +
               " </s:Body>\r\n" +
               "</s:Envelope>";
    }

    public static string BuildGetGroupVolumeEnvelope()
    {
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
               "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
               " <s:Body>\r\n" +
               $"  <u:GetGroupVolume xmlns:u=\"{SoapNsGroupRenderingControl}\">\r\n" +
               "   <InstanceID>0</InstanceID>\r\n" +
               "  </u:GetGroupVolume>\r\n" +
               " </s:Body>\r\n" +
               "</s:Envelope>";
    }

    public static string BuildSetGroupVolumeEnvelope(int volume)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
               "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
               " <s:Body>\r\n" +
               $"  <u:SetGroupVolume xmlns:u=\"{SoapNsGroupRenderingControl}\">\r\n" +
               "   <InstanceID>0</InstanceID>\r\n" +
               $"   <DesiredVolume>{clamped}</DesiredVolume>\r\n" +
               "  </u:SetGroupVolume>\r\n" +
               " </s:Body>\r\n" +
               "</s:Envelope>";
    }

    public static string BuildSnapshotGroupVolumeEnvelope()
    {
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
               "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
               " <s:Body>\r\n" +
               $"  <u:SnapshotGroupVolume xmlns:u=\"{SoapNsGroupRenderingControl}\">\r\n" +
               "   <InstanceID>0</InstanceID>\r\n" +
               "  </u:SnapshotGroupVolume>\r\n" +
               " </s:Body>\r\n" +
               "</s:Envelope>";
    }

    public static string StripScheme(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return url[7..];
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url[8..];
        return url;
    }

    public Task SetUriAndPlayAsync(SonosDevice device, string streamUrl, CancellationToken ct = default)
        => SetUriAndPlayAsync(device, streamUrl, ct, useRadioScheme: true);

    public async Task SetUriAndPlayAsync(SonosDevice device, string streamUrl, CancellationToken ct, bool useRadioScheme)
        => await SetUriAndPlayAsync(device, streamUrl, ct, useRadioScheme, "audio/aac").ConfigureAwait(false);

    public async Task SetUriAndPlayAsync(SonosDevice device, string streamUrl, CancellationToken ct, bool useRadioScheme, string contentType)
        => await SetUriAndPlayAsync(device, streamUrl, ct, useRadioScheme, contentType, metadataTitle: null).ConfigureAwait(false);

    public async Task SetUriAndPlayAsync(SonosDevice device, string streamUrl, CancellationToken ct, bool useRadioScheme, string contentType, string? metadataTitle)
    {
        string uriArg = useRadioScheme ? StripScheme(streamUrl) : streamUrl;
        var title = string.IsNullOrWhiteSpace(metadataTitle) ? $"RoomRelay - {device.FriendlyName}" : metadataTitle;
        await CallAsync(device.AvTransportControlUrl, "SetAVTransportURI", BuildSetUriEnvelope(uriArg, useRadioScheme, title, streamUrl, contentType), ct).ConfigureAwait(false);

        try
        {
            await CallAsync(device.AvTransportControlUrl, "Play", BuildPlayEnvelope(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Play command failed (speaker may already be playing): {Message}", ex.Message);
        }
    }

    public async Task StopAsync(SonosDevice device, CancellationToken ct = default)
    {
        await CallAsync(device.AvTransportControlUrl, "Stop", BuildStopEnvelope(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the volume of the whole zone group, falling back to the single
    /// player when the device has no GroupRenderingControl (issue #30).
    /// </summary>
    public async Task<int> GetVolumeAsync(SonosDevice device, CancellationToken ct = default)
    {
        if (SupportsGroupRendering(device))
        {
            try
            {
                var groupBody = await CallForBodyAsync(device.GroupRenderingControlUrl, "GetGroupVolume",
                    BuildGetGroupVolumeEnvelope(), SoapNsGroupRenderingControl, ct).ConfigureAwait(false);
                if (int.TryParse(ExtractElement(groupBody, "CurrentVolume"), out var groupVolume))
                    return Math.Clamp(groupVolume, 0, 100);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MarkNoGroupRendering(device, "GetGroupVolume", ex);
            }
        }

        var body = await CallForBodyAsync(device.RenderingControlUrl, "GetVolume", BuildGetVolumeEnvelope(), SoapNsRenderingControl, ct).ConfigureAwait(false);
        var value = ExtractElement(body, "CurrentVolume");
        if (!int.TryParse(value, out var volume))
            throw new InvalidOperationException("GetVolume response did not contain CurrentVolume.");
        return Math.Clamp(volume, 0, 100);
    }

    /// <summary>
    /// Sets the volume across every room in the zone group.
    ///
    /// RenderingControl SetVolume is per-player by definition, so on a group it
    /// only moved the coordinator and left the other rooms alone (issue #30).
    /// GroupRenderingControl scales all members against the snapshot taken when
    /// the group volume was last captured, preserving their relative levels.
    /// </summary>
    public async Task SetVolumeAsync(SonosDevice device, int volume, CancellationToken ct = default)
    {
        if (SupportsGroupRendering(device))
        {
            try
            {
                await CallForBodyAsync(device.GroupRenderingControlUrl, "SetGroupVolume",
                    BuildSetGroupVolumeEnvelope(volume), SoapNsGroupRenderingControl, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MarkNoGroupRendering(device, "SetGroupVolume", ex);
            }
        }

        await CallForBodyAsync(device.RenderingControlUrl, "SetVolume", BuildSetVolumeEnvelope(volume), SoapNsRenderingControl, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures each group member's current level so subsequent SetGroupVolume
    /// calls scale them proportionally. Best-effort: failures are logged and
    /// ignored, since SetGroupVolume still works without a fresh snapshot.
    /// </summary>
    public async Task SnapshotGroupVolumeAsync(SonosDevice device, CancellationToken ct = default)
    {
        if (!SupportsGroupRendering(device)) return;
        try
        {
            await CallForBodyAsync(device.GroupRenderingControlUrl, "SnapshotGroupVolume",
                BuildSnapshotGroupVolumeEnvelope(), SoapNsGroupRenderingControl, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkNoGroupRendering(device, "SnapshotGroupVolume", ex);
        }
    }

    private static bool SupportsGroupRendering(SonosDevice device)
    {
        lock (NoGroupLock) return !NoGroupRenderingControl.Contains(device.Udn);
    }

    private static void MarkNoGroupRendering(SonosDevice device, string action, Exception ex)
    {
        bool added;
        lock (NoGroupLock) added = NoGroupRenderingControl.Add(device.Udn);
        if (added)
            Log.Information("{Action} not available on {Name} ({Udn}); using per-player RenderingControl instead: {Message}",
                action, device.FriendlyName, device.Udn, ex.Message);
    }

    private async Task CallAsync(string controlUrl, string action, string body, CancellationToken ct)
    {
        _ = await CallForBodyAsync(controlUrl, action, body, SoapNsAvTransport, ct).ConfigureAwait(false);
    }

    private async Task<string> CallForBodyAsync(string controlUrl, string action, string body, string soapNs, CancellationToken ct)
    {
        var soapAction = $"\"{soapNs}#{action}\"";
        using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
        req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        req.Headers.TryAddWithoutValidation("SOAPACTION", soapAction);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"{action} failed: HTTP {(int)resp.StatusCode} body={text}");
        }

        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static string? ExtractElement(string xml, string tag)
    {
        var open = $"<{tag}>";
        var close = $"</{tag}>";
        int start = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += open.Length;
        int end = xml.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        return xml[start..end].Trim();
    }

    private static string XmlEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
