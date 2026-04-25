using System.Net;
using System.Text;
using Serilog;

namespace SonosStreaming.Core.Network;

public sealed class TopologyResolver : ITopologyResolver
{
    private const string SoapNsZgt = "urn:schemas-upnp-org:service:ZoneGroupTopology:1";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    // Parses ZoneGroupMember entries out of a ZoneGroupState response and
    // returns a map of UUID (without the "uuid:" prefix) to the user-set
    // zone/room name (e.g. "Living Room").
    public static Dictionary<string, string> ExtractZoneNames(string xml)
    {
        // Sonos wraps the inner ZoneGroupState XML as an entity-escaped string
        // inside the SOAP response, so `<ZoneGroupMember` arrives as
        // `&lt;ZoneGroupMember ... ZoneName=&quot;Living Room&quot;&gt;`.
        // Decode once up front so we can parse with normal attribute patterns.
        var decoded = DecodeXmlEntities(xml);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int pos = 0;
        while (pos < decoded.Length)
        {
            int memberIdx = decoded.IndexOf("<ZoneGroupMember", pos, StringComparison.OrdinalIgnoreCase);
            if (memberIdx < 0) break;
            int memberEnd = decoded.IndexOf('>', memberIdx);
            if (memberEnd < 0) break;

            var attrs = decoded[memberIdx..memberEnd];
            var uuid = ExtractAttr(attrs, "UUID");
            var name = ExtractAttr(attrs, "ZoneName");
            if (!string.IsNullOrEmpty(uuid) && !string.IsNullOrEmpty(name))
                result[uuid] = name;

            pos = memberEnd + 1;
        }
        return result;
    }

    private static string? ExtractAttr(string fragment, string name)
    {
        int idx = fragment.IndexOf(name + "=\"", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        int valStart = idx + name.Length + 2;
        int valEnd = fragment.IndexOf('"', valStart);
        if (valEnd < 0) return null;
        return fragment[valStart..valEnd];
    }

    private static string DecodeXmlEntities(string s) =>
        s.Replace("&lt;", "<").Replace("&gt;", ">")
         .Replace("&quot;", "\"").Replace("&apos;", "'")
         .Replace("&amp;", "&");

    public static HashSet<string> ExtractCoordinatorUuids(string xml)
    {
        var result = new HashSet<string>();
        var needles = new[] { "Coordinator=\"", "Coordinator=&quot;" };

        foreach (var needle in needles)
        {
            int pos = 0;
            while (pos < xml.Length)
            {
                int idx = xml.IndexOf(needle, pos, StringComparison.Ordinal);
                if (idx < 0) break;
                int tailStart = idx + needle.Length;
                int endQuote = xml.IndexOf('"', tailStart);
                int endEntity = xml.IndexOf("&quot;", tailStart, StringComparison.Ordinal);
                int end = tailStart;
                if (endQuote >= 0 && (endEntity < 0 || endQuote < endEntity))
                    end = endQuote;
                else if (endEntity >= 0)
                    end = endEntity;
                else
                    end = xml.Length;

                var uuid = xml[tailStart..end].Trim();
                if (uuid.Length > 0)
                    result.Add(uuid);
                pos = end;
            }
        }
        return result;
    }

    public async Task<List<SonosDevice>> ResolveCoordinatorsAsync(List<SonosDevice> devices, CancellationToken ct = default)
    {
        if (devices.Count == 0) return devices;

        string? topologyXml = null;
        foreach (var d in devices)
        {
            try
            {
                topologyXml = await FetchZoneGroupStateAsync(d, ct).ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "ZoneGroupTopology query to {Ip} failed", d.Ip);
            }
        }

        if (topologyXml == null)
        {
            Log.Warning("Could not reach any speaker for topology; keeping full list");
            return devices;
        }

        var coordinators = ExtractCoordinatorUuids(topologyXml);
        if (coordinators.Count == 0)
        {
            Log.Warning("No coordinators found in topology response; keeping full list");
            return devices;
        }

        var zoneNames = ExtractZoneNames(topologyXml);

        return devices
            .Where(d => coordinators.Contains(StripUuidPrefix(d.Udn)))
            .Select(d =>
            {
                var uuid = StripUuidPrefix(d.Udn);
                return zoneNames.TryGetValue(uuid, out var room)
                    ? d with { FriendlyName = room }
                    : d;
            })
            .ToList();
    }

    private static string StripUuidPrefix(string udn) =>
        udn.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? udn[5..] : udn;

    private async Task<string> FetchZoneGroupStateAsync(SonosDevice device, CancellationToken ct)
    {
        var url = $"http://{device.Ip}:{device.Port}/ZoneGroupTopology/Control";
        var soapAction = $"\"{SoapNsZgt}#GetZoneGroupState\"";
        var body = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                   "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                   " <s:Body>\r\n" +
                   $"  <u:GetZoneGroupState xmlns:u=\"{SoapNsZgt}\"></u:GetZoneGroupState>\r\n" +
                   " </s:Body>\r\n" +
                   "</s:Envelope>";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        req.Headers.TryAddWithoutValidation("SOAPACTION", soapAction);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
