using SonosStreaming.Core.Network;
using FluentAssertions;
using Xunit;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SonosStreaming.Tests;

public class SsdpParserTests
{
    [Fact]
    public void ParseCannedSonosResponse()
    {
        var raw = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "CACHE-CONTROL: max-age=1800\r\n" +
            "EXT:\r\n" +
            "LOCATION: http://192.168.1.42:1400/xml/device_description.xml\r\n" +
            "SERVER: Linux UPnP/1.0 Sonos/69.2\r\n" +
            "ST: urn:schemas-upnp-org:device:ZonePlayer:1\r\n\r\n");

        var headers = SsdpDiscovery.ParseSsdpResponse(raw);
        headers.Should().NotBeNull();
        headers!["LOCATION"].Should().Be("http://192.168.1.42:1400/xml/device_description.xml");
        headers["ST"].Should().Be("urn:schemas-upnp-org:device:ZonePlayer:1");
    }

    [Fact]
    public void RejectsNon200()
    {
        var raw = Encoding.ASCII.GetBytes("HTTP/1.1 500 Internal Server Error\r\n\r\n");
        var result = SsdpDiscovery.ParseSsdpResponse(raw);
        result.Should().BeNull();
    }

    [Fact]
    public void ParseLocation()
    {
        var parsed = SsdpDiscovery.ParseLocation("http://192.168.1.42:1400/xml/device_description.xml");
        parsed.Should().NotBeNull();
        parsed!.Value.Ip.ToString().Should().Be("192.168.1.42");
        parsed.Value.Port.Should().Be(1400);
    }

    [Fact]
    public void ParseDeviceDescription()
    {
        var xml = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><friendlyName>Kitchen - Sonos Play:1</friendlyName><UDN>uuid:RINCON_AABBCCDDEEFF01400</UDN></device></root>";
        var desc = SsdpDiscovery.ParseDeviceDescription(xml);
        desc.Should().NotBeNull();
        desc!.Value.FriendlyName.Should().Be("Kitchen - Sonos Play:1");
        desc.Value.Udn.Should().Be("uuid:RINCON_AABBCCDDEEFF01400");
    }

    [Fact]
    public async Task LookupAsync_FetchesDeviceDescriptionFromCustomPort()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            var buffer = new byte[2048];
            _ = await stream.ReadAsync(buffer, ct);

            var xml = "<?xml version=\"1.0\"?><root><device><friendlyName>Office</friendlyName><UDN>uuid:RINCON_TEST01400</UDN></device></root>";
            var body = Encoding.UTF8.GetBytes(xml);
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Content-Type: text/xml\r\n\r\n");
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(body, ct);
        }, ct);

        using var discovery = new SsdpDiscovery();
        var device = await discovery.LookupAsync(IPAddress.Loopback, port, ct);

        device.FriendlyName.Should().Be("Office");
        device.Udn.Should().Be("uuid:RINCON_TEST01400");
        device.Ip.Should().Be(IPAddress.Loopback);
        device.Port.Should().Be(port);
        await serverTask;
    }

    [Fact]
    public async Task LookupAsync_RejectsNonSonosUdn()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            var buffer = new byte[2048];
            _ = await stream.ReadAsync(buffer, ct);

            var xml = "<?xml version=\"1.0\"?><root><device><friendlyName>Hue Bridge</friendlyName><UDN>uuid:SomeOtherDevice-1234</UDN></device></root>";
            var body = Encoding.UTF8.GetBytes(xml);
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Content-Type: text/xml\r\n\r\n");
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(body, ct);
        }, ct);

        using var discovery = new SsdpDiscovery();
        var act = () => discovery.LookupAsync(IPAddress.Loopback, port, ct);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a Sonos speaker*");
        await serverTask;
    }

    [Fact]
    public void MergeDevices_DeduplicatesByUdnAndEndpoint()
    {
        var ssdp = new[]
        {
            new SonosDevice("Kitchen", IPAddress.Parse("192.168.1.10"), 1400, "uuid:RINCON_A"),
            new SonosDevice("Office", IPAddress.Parse("192.168.1.11"), 1400, "uuid:RINCON_B"),
        };
        var manual = new[]
        {
            new SonosDevice("Kitchen duplicate", IPAddress.Parse("192.168.1.99"), 1400, "uuid:RINCON_A"),
            new SonosDevice("Office duplicate", IPAddress.Parse("192.168.1.11"), 1400, "uuid:OTHER"),
            new SonosDevice("Bedroom", IPAddress.Parse("192.168.1.12"), 1500, "uuid:RINCON_C"),
        };

        var merged = SsdpDiscovery.MergeDevices(ssdp, manual);

        merged.Should().HaveCount(3);
        merged.Select(d => d.FriendlyName).Should().Equal("Kitchen", "Office", "Bedroom");
    }

    [Fact]
    public void FormatHost_BracketsIpv6Addresses()
    {
        SsdpDiscovery.FormatHost(IPAddress.Parse("fe80::1")).Should().Be("[fe80::1]");
        SsdpDiscovery.FormatHost(IPAddress.Parse("192.168.1.42")).Should().Be("192.168.1.42");
    }

    [Fact]
    public void NonSonosUdn_DoesNotStartWithRinconPrefix()
    {
        var xml = "<?xml version=\"1.0\"?><root><device><friendlyName>Hue Bridge</friendlyName><UDN>uuid:SomeOtherDevice-1234</UDN></device></root>";
        var desc = SsdpDiscovery.ParseDeviceDescription(xml);
        desc.Should().NotBeNull();
        desc!.Value.Udn.Should().Be("uuid:SomeOtherDevice-1234");
        desc.Value.Udn.StartsWith("uuid:RINCON_").Should().BeFalse();
    }

    [Fact]
    public void SonosUdn_StartsWithRinconPrefix()
    {
        var xml = "<?xml version=\"1.0\"?><root><device><friendlyName>Kitchen</friendlyName><UDN>uuid:RINCON_AABBCCDDEEFF01400</UDN></device></root>";
        var desc = SsdpDiscovery.ParseDeviceDescription(xml);
        desc.Should().NotBeNull();
        desc!.Value.Udn.Should().StartWith("uuid:RINCON_");
    }
}
