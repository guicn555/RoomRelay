using SonosStreaming.Core.Network;
using FluentAssertions;
using Xunit;
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
}
