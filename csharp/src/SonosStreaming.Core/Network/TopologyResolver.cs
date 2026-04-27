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

    public static List<SonosZoneGroup> ExtractZoneGroups(string xml)
    {
        var decoded = DecodeXmlEntities(xml);
        var result = new List<SonosZoneGroup>();
        int pos = 0;
        while (pos < decoded.Length)
        {
            int groupIdx = decoded.IndexOf("<ZoneGroup", pos, StringComparison.OrdinalIgnoreCase);
            if (groupIdx < 0) break;

            int startEnd = decoded.IndexOf('>', groupIdx);
            if (startEnd < 0) break;

            var attrs = decoded[groupIdx..startEnd];
            var coordinator = ExtractAttr(attrs, "Coordinator");
            if (string.IsNullOrWhiteSpace(coordinator))
            {
                pos = startEnd + 1;
                continue;
            }

            int closeIdx = decoded.IndexOf("</ZoneGroup>", startEnd, StringComparison.OrdinalIgnoreCase);
            int groupEnd = closeIdx >= 0 ? closeIdx : startEnd;
            var groupXml = decoded[startEnd..groupEnd];
            var members = ExtractGroupMembers(groupXml);
            if (members.Count == 0)
                members.Add(new SonosZoneMember(coordinator, null, null, null, null));

            result.Add(new SonosZoneGroup(coordinator, members));
            pos = closeIdx >= 0 ? closeIdx + "</ZoneGroup>".Length : startEnd + 1;
        }

        return result;
    }

    private static List<SonosZoneMember> ExtractGroupMembers(string groupXml)
    {
        var members = new List<SonosZoneMember>();
        int pos = 0;
        while (pos < groupXml.Length)
        {
            int memberIdx = groupXml.IndexOf("<ZoneGroupMember", pos, StringComparison.OrdinalIgnoreCase);
            if (memberIdx < 0) break;
            int memberEnd = groupXml.IndexOf('>', memberIdx);
            if (memberEnd < 0) break;

            var attrs = groupXml[memberIdx..memberEnd];
            var uuid = ExtractAttr(attrs, "UUID");
            if (!string.IsNullOrWhiteSpace(uuid))
            {
                var zoneName = ExtractAttr(attrs, "ZoneName");
                var location = ExtractAttr(attrs, "Location");
                var invisible = ExtractAttr(attrs, "Invisible");
                var configuration = ExtractAttr(attrs, "Configuration");
                members.Add(new SonosZoneMember(uuid, zoneName, location, invisible, configuration));
            }

            pos = memberEnd + 1;
        }

        return members;
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

        var topologyXmls = new List<string>();
        var knownUuids = devices.Select(d => StripUuidPrefix(d.Udn)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coveredUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            var deviceUuid = StripUuidPrefix(d.Udn);
            if (coveredUuids.Contains(deviceUuid) && coveredUuids.Count >= knownUuids.Count)
                break;

            try
            {
                var topologyXml = await FetchZoneGroupStateAsync(d, ct).ConfigureAwait(false);
                topologyXmls.Add(topologyXml);

                foreach (var group in ExtractZoneGroups(topologyXml))
                foreach (var member in group.Members)
                    coveredUuids.Add(member.Uuid);

                Log.Debug("Resolved topology from {Name} ({Ip}); covered {Covered}/{Known} discovered UUID(s)",
                    d.FriendlyName, d.Ip, coveredUuids.Count(u => knownUuids.Contains(u)), knownUuids.Count);

                if (knownUuids.All(coveredUuids.Contains))
                    break;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "ZoneGroupTopology query to {Ip} failed", d.Ip);
            }
        }

        if (topologyXmls.Count == 0)
        {
            Log.Warning("Could not reach any speaker for topology; keeping full list");
            return devices;
        }

        var resolved = ResolveDevicesFromTopologies(devices, topologyXmls);
        if (resolved.Count == 0)
        {
            Log.Warning("No rooms found in topology response; keeping full list");
            return devices;
        }

        Log.Information("Topology resolved {InputCount} discovered speaker(s) to {OutputCount} selectable room/group(s)",
            devices.Count, resolved.Count);
        return resolved;
    }

    public static List<SonosDevice> ResolveDevicesFromTopologies(List<SonosDevice> devices, IEnumerable<string> topologyXmls)
    {
        var byUuid = devices.ToDictionary(d => StripUuidPrefix(d.Udn), StringComparer.OrdinalIgnoreCase);
        var output = new List<SonosDevice>();
        var emittedUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coveredUuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var topologyXml in topologyXmls)
        {
            foreach (var group in ExtractZoneGroups(topologyXml))
            {
                foreach (var member in group.Members)
                    coveredUuids.Add(member.Uuid);

                if (!group.Members.Any(m => byUuid.ContainsKey(m.Uuid)) && !byUuid.ContainsKey(group.CoordinatorUuid))
                    continue;

                if (emittedUuids.Contains(group.CoordinatorUuid))
                    continue;

                var device = BuildGroupDevice(group, byUuid);
                if (device == null)
                    continue;

                output.Add(device);
                emittedUuids.Add(group.CoordinatorUuid);
            }
        }

        foreach (var device in devices)
        {
            var uuid = StripUuidPrefix(device.Udn);
            if (coveredUuids.Contains(uuid))
                continue;
            if (emittedUuids.Add(uuid))
                output.Add(device);
        }

        return output;
    }

    private static SonosDevice? BuildGroupDevice(SonosZoneGroup group, Dictionary<string, SonosDevice> byUuid)
    {
        byUuid.TryGetValue(group.CoordinatorUuid, out var coordinatorDevice);
        var coordinatorMember = group.Members.FirstOrDefault(m => string.Equals(m.Uuid, group.CoordinatorUuid, StringComparison.OrdinalIgnoreCase));
        var endpoint = coordinatorDevice ?? DeviceFromMember(coordinatorMember, group.CoordinatorUuid);
        if (endpoint == null)
            return null;

        var memberNames = group.Members
            .Select(m => !string.IsNullOrWhiteSpace(m.ZoneName) ? m.ZoneName! : byUuid.GetValueOrDefault(m.Uuid)?.FriendlyName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fallbackName = coordinatorDevice?.FriendlyName ?? endpoint.FriendlyName;
        var name = memberNames.Count switch
        {
            0 => fallbackName,
            1 => memberNames[0],
            <= 3 => string.Join(" + ", memberNames),
            _ => $"{memberNames[0]} + {memberNames.Count - 1} rooms",
        };

        return endpoint with { FriendlyName = name, Udn = $"uuid:{group.CoordinatorUuid}" };
    }

    private static SonosDevice? DeviceFromMember(SonosZoneMember? member, string uuid)
    {
        if (member == null || string.IsNullOrWhiteSpace(member.Location))
            return null;

        var parsed = SsdpDiscovery.ParseLocation(member.Location);
        if (parsed == null)
            return null;

        return new SonosDevice(member.ZoneName ?? $"Sonos {uuid}", parsed.Value.Ip, parsed.Value.Port, $"uuid:{uuid}");
    }

    public static string StripUuidPrefix(string udn) =>
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

public sealed record SonosZoneMember(string Uuid, string? ZoneName, string? Location, string? Invisible, string? Configuration);

public sealed record SonosZoneGroup(string CoordinatorUuid, List<SonosZoneMember> Members);
