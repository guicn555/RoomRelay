using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Serilog;

namespace SonosStreaming.Core.Network;

public sealed class SsdpDiscovery : ISsdpDiscovery
{
    private static readonly IPAddress SsdpAddrV4 = IPAddress.Parse("239.255.255.250");
    private static readonly IPAddress SsdpAddrV6 = IPAddress.Parse("ff02::c");
    private const int SsdpPort = 1900;
    private const string SonosSt = "urn:schemas-upnp-org:device:ZonePlayer:1";
    private const string SonosUdnPrefix = "uuid:RINCON_";

    private readonly HttpClient _http;
    private readonly TimeSpan _httpTimeout = TimeSpan.FromSeconds(3);

    public SsdpDiscovery()
    {
        _http = new HttpClient { Timeout = _httpTimeout };
    }

    public static byte[] BuildMSearch(IPAddress multicastAddr, string st = SonosSt, uint mx = 2)
    {
        return Encoding.ASCII.GetBytes(
            $"M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {multicastAddr}:{SsdpPort}\r\n" +
            $"MAN: \"ssdp:discover\"\r\n" +
            $"MX: {mx}\r\n" +
            $"ST: {st}\r\n\r\n");
    }

    public static Dictionary<string, string>? ParseSsdpResponse(ReadOnlySpan<byte> buf)
    {
        var s = Encoding.UTF8.GetString(buf);
        var lines = s.Split("\r\n");
        if (lines.Length == 0) return null;

        var status = lines[0].Trim().ToUpperInvariant();
        if (!status.StartsWith("HTTP/1.1 200") && !status.StartsWith("HTTP/1.0 200"))
            return null;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrEmpty(line)) continue;
            var colonIdx = line.IndexOf(':');
            if (colonIdx < 0) continue;
            map[line[..colonIdx].Trim().ToUpperInvariant()] = line[(colonIdx + 1)..].Trim();
        }
        return map;
    }

    public static (IPAddress Ip, ushort Port)? ParseLocation(string loc)
    {
        var rest = loc.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? loc[7..] : null;
        if (rest == null) return null;
        int slashIdx = rest.IndexOf('/');
        var authority = slashIdx >= 0 ? rest[..slashIdx] : rest;

        // IPv6 bracket notation: [fe80::1]:1400
        if (authority.StartsWith("["))
        {
            int bracketEnd = authority.IndexOf(']');
            if (bracketEnd < 0) return null;
            var ipStr = authority[1..bracketEnd];
            var portStr = authority.Length > bracketEnd + 2 && authority[bracketEnd + 1] == ':'
                ? authority[(bracketEnd + 2)..]
                : null;
            if (portStr == null) return null;
            if (!IPAddress.TryParse(ipStr, out var ip)) return null;
            if (!ushort.TryParse(portStr, out var port)) return null;
            return (ip, port);
        }

        var colonIdx = authority.IndexOf(':');
        if (colonIdx < 0) return null;
        if (!IPAddress.TryParse(authority[..colonIdx], out var ip4)) return null;
        if (!ushort.TryParse(authority[(colonIdx + 1)..], out var port4)) return null;
        return (ip4, port4);
    }

    public static string FormatHost(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();

    public static (string FriendlyName, string Udn)? ParseDeviceDescription(string xml)
    {
        var name = ExtractElement(xml, "friendlyName");
        var udn = ExtractElement(xml, "UDN");
        if (name == null || udn == null) return null;
        return (name, udn);
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

    public async Task<SonosDevice> LookupAsync(IPAddress ip, ushort port = 1400, CancellationToken ct = default)
    {
        var url = $"http://{FormatHost(ip)}:{port}/xml/device_description.xml";
        var resp = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        var desc = ParseDeviceDescription(resp);
        if (desc == null)
            throw new InvalidOperationException($"Device description from {ip}:{port} did not contain a Sonos friendlyName and UDN.");
        if (!desc.Value.Udn.StartsWith(SonosUdnPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Device at {ip}:{port} has UDN \"{desc.Value.Udn}\" which is not a Sonos speaker.");

        return new SonosDevice(desc.Value.FriendlyName, ip, port, desc.Value.Udn);
    }

    public static List<SonosDevice> MergeDevices(IEnumerable<SonosDevice> primary, IEnumerable<SonosDevice> fallback)
    {
        var merged = new List<SonosDevice>();
        foreach (var device in primary.Concat(fallback))
        {
            if (merged.Any(existing => IsSameDevice(existing, device)))
                continue;

            merged.Add(device);
        }

        return merged;
    }

    private static bool IsSameDevice(SonosDevice left, SonosDevice right)
    {
        if (!string.IsNullOrWhiteSpace(left.Udn) &&
            !string.IsNullOrWhiteSpace(right.Udn) &&
            string.Equals(left.Udn, right.Udn, StringComparison.OrdinalIgnoreCase))
            return true;

        return left.Ip.Equals(right.Ip) && left.Port == right.Port;
    }

    public async Task<List<SonosDevice>> ScanAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        uint mx = (uint)Math.Clamp(timeoutMs / 1000, 1, 5);
        var ifaces = CandidateInterfaces();

        if (ifaces.Count == 0)
            ifaces.Add((IPAddress.Any, AddressFamily.InterNetwork, "Any"));

        Log.Debug("SSDP scanning on interfaces: {Ifaces}", ifaces.Select(i => $"{i.Name}={i.Ip}"));

        var sockets = new List<(UdpClient Client, AddressFamily Family)>();
        foreach (var (ip, family, _) in ifaces)
        {
            try
            {
                var sock = new UdpClient(new IPEndPoint(ip, 0));
                sock.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                IPAddress multicastAddr;
                if (family == AddressFamily.InterNetworkV6)
                {
                    multicastAddr = SsdpAddrV6;
                    sock.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 2);
                    sock.Client.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
                        new IPv6MulticastOption(multicastAddr, ip.ScopeId));
                }
                else
                {
                    multicastAddr = SsdpAddrV4;
                    sock.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, ip.GetAddressBytes());
                    sock.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                }

                var msg = BuildMSearch(multicastAddr, SonosSt, mx);
                await sock.SendAsync(msg, new IPEndPoint(multicastAddr, SsdpPort), ct).ConfigureAwait(false);
                sockets.Add((sock, family));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "SSDP bind on {Ip} failed", ip);
            }
        }

        if (sockets.Count == 0)
            throw new InvalidOperationException("No usable interface for SSDP");

        var locations = new Dictionary<string, (IPAddress Ip, ushort Port)>();
        var locLock = new object();

        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(timeoutMs);
        var token = deadlineCts.Token;

        var receiveTasks = sockets.Select(async pair =>
        {
            var (sock, _) = pair;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var result = await sock.ReceiveAsync(token).ConfigureAwait(false);
                    var headers = ParseSsdpResponse(result.Buffer);
                    if (headers != null && headers.TryGetValue("LOCATION", out var loc))
                    {
                        var parsed = ParseLocation(loc);
                        if (parsed != null)
                        {
                            lock (locLock)
                                locations.TryAdd(loc, parsed.Value);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { Log.Debug(ex, "SSDP receive error"); }
            }
        }).ToArray();

        try { await Task.WhenAll(receiveTasks).ConfigureAwait(false); }
        catch { }

        foreach (var (sock, _) in sockets) sock.Dispose();

        Log.Debug("SSDP discovered {Count} location(s)", locations.Count);

        var devices = new List<SonosDevice>();
        foreach (var (loc, (ip, port)) in locations)
        {
            try
            {
                var resp = await _http.GetStringAsync(loc, ct).ConfigureAwait(false);
                var desc = ParseDeviceDescription(resp);
                if (desc != null && desc.Value.Udn.StartsWith(SonosUdnPrefix, StringComparison.OrdinalIgnoreCase))
                    devices.Add(new SonosDevice(desc.Value.FriendlyName, ip, port, desc.Value.Udn));
            }
            catch { }
        }

        return devices;
    }

    private static List<(IPAddress Ip, AddressFamily Family, string Name)> CandidateInterfaces()
    {
        var result = new List<(IPAddress, AddressFamily, string)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var addr = ua.Address;
                    if (addr.Equals(IPAddress.Loopback)) continue;
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var bytes = addr.GetAddressBytes();
                        if (bytes[0] == 169 && bytes[1] == 254) continue; // link-local
                        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) continue;
                        result.Add((addr, AddressFamily.InterNetwork, ni.Name));
                    }
                    else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (addr.IsIPv6LinkLocal) continue;
                        result.Add((addr, AddressFamily.InterNetworkV6, ni.Name));
                    }
                }
            }
        }
        catch { }
        return result;
    }

    public void Dispose() => _http.Dispose();
}

public sealed record SonosDevice(string FriendlyName, IPAddress Ip, ushort Port, string Udn)
{
    public string AvTransportControlUrl => $"http://{SsdpDiscovery.FormatHost(Ip)}:{Port}/MediaRenderer/AVTransport/Control";
    public string RenderingControlUrl => $"http://{SsdpDiscovery.FormatHost(Ip)}:{Port}/MediaRenderer/RenderingControl/Control";
}
