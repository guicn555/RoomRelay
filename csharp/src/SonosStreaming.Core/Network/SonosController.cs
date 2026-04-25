using System.Net;
using System.Text;
using Serilog;

namespace SonosStreaming.Core.Network;

public sealed class SonosController : ISonosController
{
    private const string SoapNsAvTransport = "urn:schemas-upnp-org:service:AVTransport:1";
    private readonly HttpClient _http;
    public SonosController()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public static string BuildSetUriEnvelope(string streamUrl, bool useRadioScheme = true)
    {
        var escaped = XmlEscape(streamUrl);
        // x-rincon-mp3radio:// forces Sonos into its MPEG radio decoder.
        // For LPCM (audio/L16) we send the plain http:// URI so Sonos
        // respects the Content-Type header and picks the PCM decoder.
        var currentUri = useRadioScheme ? $"x-rincon-mp3radio://{escaped}" : escaped;
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
               "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
               " <s:Body>\r\n" +
               $"  <u:SetAVTransportURI xmlns:u=\"{SoapNsAvTransport}\">\r\n" +
               "   <InstanceID>0</InstanceID>\r\n" +
               $"   <CurrentURI>{currentUri}</CurrentURI>\r\n" +
               "   <CurrentURIMetaData></CurrentURIMetaData>\r\n" +
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

    public static string StripScheme(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return url[7..];
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url[8..];
        return url;
    }

    public Task SetUriAndPlayAsync(SonosDevice device, string streamUrl, CancellationToken ct = default)
        => SetUriAndPlayAsync(device, streamUrl, ct, useRadioScheme: true);

    public async Task SetUriAndPlayAsync(SonosDevice device, string streamUrl, CancellationToken ct, bool useRadioScheme)
    {
        string uriArg = useRadioScheme ? StripScheme(streamUrl) : streamUrl;
        await CallAsync(device.AvTransportControlUrl, "SetAVTransportURI", BuildSetUriEnvelope(uriArg, useRadioScheme), ct).ConfigureAwait(false);

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

    private async Task CallAsync(string controlUrl, string action, string body, CancellationToken ct)
    {
        var soapAction = $"\"{SoapNsAvTransport}#{action}\"";
        using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
        req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        req.Headers.TryAddWithoutValidation("SOAPACTION", soapAction);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"{action} failed: HTTP {(int)resp.StatusCode} body={text}");
        }
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
