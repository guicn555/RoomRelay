using SonosStreaming.Core.Network;
using FluentAssertions;
using Xunit;

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
    public void StripUuidPrefix_WithPrefix_RemovesIt()
    {
        // Access via reflection since it's private
        var method = typeof(TopologyResolver).GetMethod("StripUuidPrefix",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull();
        var result = method!.Invoke(null, ["uuid:RINCON_ABC"]);
        result.Should().Be("RINCON_ABC");
    }

    [Fact]
    public void StripUuidPrefix_WithoutPrefix_ReturnsAsIs()
    {
        var method = typeof(TopologyResolver).GetMethod("StripUuidPrefix",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method!.Invoke(null, ["RINCON_ABC"]);
        result.Should().Be("RINCON_ABC");
    }
}
