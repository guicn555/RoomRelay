using SonosStreaming.Core.Network;
using FluentAssertions;
using Xunit;
using System.Net;

namespace SonosStreaming.Tests;

public class TopologyResolverTests
{
    [Fact]
    public void ExtractZoneNames_EmptyInput_ReturnsEmpty()
    {
        var result = TopologyResolver.ExtractZoneNames("");
        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractZoneNames_SingleMember_ExtractsName()
    {
        var xml = "<root><device><ZoneGroupMember UUID=\"RINCON_ABC\" ZoneName=\"Kitchen\"></ZoneGroupMember></device></root>";
        var result = TopologyResolver.ExtractZoneNames(xml);
        result.Should().ContainKey("RINCON_ABC").WhoseValue.Should().Be("Kitchen");
    }

    [Fact]
    public void ExtractZoneNames_EscapedEntities_DecodesCorrectly()
    {
        var xml = "&lt;ZoneGroupMember UUID=\"RINCON_XYZ\" ZoneName=\"Living Room\"&gt;";
        var result = TopologyResolver.ExtractZoneNames(xml);
        result.Should().ContainKey("RINCON_XYZ").WhoseValue.Should().Be("Living Room");
    }

    [Fact]
    public void ExtractCoordinatorUuids_Empty_ReturnsEmpty()
    {
        var result = TopologyResolver.ExtractCoordinatorUuids("");
        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractCoordinatorUuids_MultipleGroups_ReturnsOnlyCoordinators()
    {
        var xml = """<ZoneGroup Coordinator="RINCON_AAA" ID="RINCON_AAA:1"><ZoneGroupMember UUID="RINCON_AAA"/><ZoneGroupMember UUID="RINCON_BBB"/></ZoneGroup><ZoneGroup Coordinator="RINCON_CCC" ID="RINCON_CCC:2"><ZoneGroupMember UUID="RINCON_CCC"/></ZoneGroup>""";
        var result = TopologyResolver.ExtractCoordinatorUuids(xml);
        result.Should().Contain("RINCON_AAA");
        result.Should().Contain("RINCON_CCC");
        result.Should().NotContain("RINCON_BBB");
    }

    [Fact]
    public void ExtractZoneGroups_MultipleGroups_ExtractsMembersAndNames()
    {
        var xml = """<ZoneGroups><ZoneGroup Coordinator="RINCON_A" ID="RINCON_A:1"><ZoneGroupMember UUID="RINCON_A" ZoneName="Living Room" Location="http://192.168.1.10:1400/xml/device_description.xml"/><ZoneGroupMember UUID="RINCON_B" ZoneName="Kitchen" Location="http://192.168.1.11:1400/xml/device_description.xml"/></ZoneGroup><ZoneGroup Coordinator="RINCON_C" ID="RINCON_C:2"><ZoneGroupMember UUID="RINCON_C" ZoneName="Office"/></ZoneGroup></ZoneGroups>""";

        var groups = TopologyResolver.ExtractZoneGroups(xml);

        groups.Should().HaveCount(2);
        groups[0].CoordinatorUuid.Should().Be("RINCON_A");
        groups[0].Members.Select(m => m.ZoneName).Should().Equal("Living Room", "Kitchen");
    }

    [Fact]
    public void ResolveDevicesFromTopologies_UnionsMixedHouseholds()
    {
        var devices = new List<SonosDevice>
        {
            new("S1 Living Room", IPAddress.Parse("192.168.1.10"), 1400, "uuid:RINCON_S1A"),
            new("S2 Office", IPAddress.Parse("192.168.1.20"), 1400, "uuid:RINCON_S2A"),
        };
        var s1 = """<ZoneGroups><ZoneGroup Coordinator="RINCON_S1A"><ZoneGroupMember UUID="RINCON_S1A" ZoneName="Living Room"/></ZoneGroup></ZoneGroups>""";
        var s2 = """<ZoneGroups><ZoneGroup Coordinator="RINCON_S2A"><ZoneGroupMember UUID="RINCON_S2A" ZoneName="Office"/></ZoneGroup></ZoneGroups>""";

        var resolved = TopologyResolver.ResolveDevicesFromTopologies(devices, [s1, s2]);

        resolved.Should().HaveCount(2);
        resolved.Select(d => d.FriendlyName).Should().BeEquivalentTo("Living Room", "Office");
    }

    [Fact]
    public void ResolveDevicesFromTopologies_CollapsesGroupToCoordinatorLabel()
    {
        var devices = new List<SonosDevice>
        {
            new("Living Room raw", IPAddress.Parse("192.168.1.10"), 1400, "uuid:RINCON_A"),
            new("Kitchen raw", IPAddress.Parse("192.168.1.11"), 1400, "uuid:RINCON_B"),
        };
        var xml = """<ZoneGroups><ZoneGroup Coordinator="RINCON_A"><ZoneGroupMember UUID="RINCON_A" ZoneName="Living Room"/><ZoneGroupMember UUID="RINCON_B" ZoneName="Kitchen"/></ZoneGroup></ZoneGroups>""";

        var resolved = TopologyResolver.ResolveDevicesFromTopologies(devices, [xml]);

        resolved.Should().ContainSingle();
        resolved[0].Udn.Should().Be("uuid:RINCON_A");
        resolved[0].Ip.Should().Be(IPAddress.Parse("192.168.1.10"));
        resolved[0].FriendlyName.Should().Be("Living Room + Kitchen");
    }

    [Fact]
    public void ResolveDevicesFromTopologies_PreservesUnknownDevice()
    {
        var devices = new List<SonosDevice>
        {
            new("Known", IPAddress.Parse("192.168.1.10"), 1400, "uuid:RINCON_A"),
            new("Manual", IPAddress.Parse("192.168.1.99"), 1400, "uuid:RINCON_MANUAL"),
        };
        var xml = """<ZoneGroups><ZoneGroup Coordinator="RINCON_A"><ZoneGroupMember UUID="RINCON_A" ZoneName="Known"/></ZoneGroup></ZoneGroups>""";

        var resolved = TopologyResolver.ResolveDevicesFromTopologies(devices, [xml]);

        resolved.Select(d => d.Udn).Should().Contain(["uuid:RINCON_A", "uuid:RINCON_MANUAL"]);
    }

    [Fact]
    public void StripUuidPrefix_WithPrefix_RemovesIt()
    {
        TopologyResolver.StripUuidPrefix("uuid:RINCON_ABC").Should().Be("RINCON_ABC");
    }

    [Fact]
    public void StripUuidPrefix_WithoutPrefix_ReturnsAsIs()
    {
        TopologyResolver.StripUuidPrefix("RINCON_ABC").Should().Be("RINCON_ABC");
    }

    [Fact]
    public void BuildZoneGroupTopologyControlUrl_WithIPv6_BracketsHost()
    {
        var device = new SonosDevice("Office", IPAddress.Parse("fe80::1"), 1400, "uuid:RINCON_ABC");

        TopologyResolver.BuildZoneGroupTopologyControlUrl(device)
            .Should().Be("http://[fe80::1]:1400/ZoneGroupTopology/Control");
    }
}
