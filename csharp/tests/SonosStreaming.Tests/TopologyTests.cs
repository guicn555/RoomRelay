using SonosStreaming.Core.Network;
using FluentAssertions;
using Xunit;

namespace SonosStreaming.Tests;

public class TopologyTests
{
    [Fact]
    public void ExtractCoordinatorUuids_FromRawAttrs()
    {
        var xml = """<ZoneGroup Coordinator="RINCON_AAA01400" ID="RINCON_AAA01400:123"><ZoneGroupMember UUID="RINCON_AAA01400"/><ZoneGroupMember UUID="RINCON_BBB01400"/></ZoneGroup><ZoneGroup Coordinator="RINCON_CCC01400" ID="RINCON_CCC01400:456"><ZoneGroupMember UUID="RINCON_CCC01400"/></ZoneGroup>""";
        var uuids = TopologyResolver.ExtractCoordinatorUuids(xml);
        uuids.Should().Contain("RINCON_AAA01400");
        uuids.Should().Contain("RINCON_CCC01400");
        uuids.Should().NotContain("RINCON_BBB01400");
        uuids.Should().HaveCount(2);
    }

    [Fact]
    public void ExtractCoordinatorUuids_FromEscapedBody()
    {
        var xml = """<ZoneGroupState>&lt;ZoneGroups&gt;&lt;ZoneGroup Coordinator=&quot;RINCON_XYZ01400&quot; ID=&quot;RINCON_XYZ01400:1&quot;&gt;&lt;/ZoneGroup&gt;&lt;/ZoneGroups&gt;</ZoneGroupState>""";
        var uuids = TopologyResolver.ExtractCoordinatorUuids(xml);
        uuids.Should().Contain("RINCON_XYZ01400");
    }

    [Fact]
    public void ExtractCoordinatorUuids_EmptyInput()
    {
        TopologyResolver.ExtractCoordinatorUuids("").Should().BeEmpty();
        TopologyResolver.ExtractCoordinatorUuids("<nothing/>").Should().BeEmpty();
    }
}
