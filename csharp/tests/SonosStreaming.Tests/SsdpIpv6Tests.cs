using System.Net;
using SonosStreaming.Core.Network;
using FluentAssertions;
using Xunit;

namespace SonosStreaming.Tests;

public class SsdpIpv6Tests
{
    [Fact]
    public void BuildMSearch_WithIPv6Multicast_ContainsCorrectHost()
    {
        var msg = SsdpDiscovery.BuildMSearch(IPAddress.Parse("ff02::c"), "urn:schemas-upnp-org:device:ZonePlayer:1", 2);
        var text = System.Text.Encoding.ASCII.GetString(msg);
        text.Should().Contain("HOST: ff02::c:1900");
    }

    [Fact]
    public void BuildMSearch_WithIPv4Multicast_ContainsCorrectHost()
    {
        var msg = SsdpDiscovery.BuildMSearch(IPAddress.Parse("239.255.255.250"), "urn:schemas-upnp-org:device:ZonePlayer:1", 2);
        var text = System.Text.Encoding.ASCII.GetString(msg);
        text.Should().Contain("HOST: 239.255.255.250:1900");
    }

    [Fact]
    public void ParseLocation_IPv4_Works()
    {
        var parsed = SsdpDiscovery.ParseLocation("http://192.168.1.42:1400/xml/device_description.xml");
        parsed.Should().NotBeNull();
        parsed!.Value.Ip.ToString().Should().Be("192.168.1.42");
        parsed.Value.Port.Should().Be(1400);
    }

    [Fact]
    public void ParseLocation_IPv6_Works()
    {
        var parsed = SsdpDiscovery.ParseLocation("http://[fe80::1]:1400/xml/device_description.xml");
        parsed.Should().NotBeNull();
        parsed!.Value.Ip.ToString().Should().Be("fe80::1");
        parsed.Value.Port.Should().Be(1400);
    }

    [Fact]
    public void ParseLocation_NoScheme_ReturnsNull()
    {
        var parsed = SsdpDiscovery.ParseLocation("192.168.1.42:1400/xml");
        parsed.Should().BeNull();
    }

    [Fact]
    public void ParseLocation_NoPort_ReturnsNull()
    {
        var parsed = SsdpDiscovery.ParseLocation("http://192.168.1.42/xml");
        parsed.Should().BeNull();
    }
}
