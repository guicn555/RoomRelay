using System.Net;
using FluentAssertions;
using SonosStreaming.Core.Network;
using Xunit;

namespace SonosStreaming.Tests;

// Issue #30: RenderingControl SetVolume is per-player, so on a zone group it
// only moved the coordinator. Group-wide changes go through
// GroupRenderingControl on the coordinator instead.
public sealed class GroupVolumeTests
{
    private static SonosDevice Device() =>
        new("Room 2 + Room 1", IPAddress.Parse("192.168.1.10"), 1400, "uuid:RINCON_000E58000001400");

    [Fact]
    public void GroupRenderingControlUrl_PointsAtTheGroupService()
    {
        Device().GroupRenderingControlUrl
            .Should().Be("http://192.168.1.10:1400/MediaRenderer/GroupRenderingControl/Control");
    }

    [Fact]
    public void GroupRenderingControlUrl_BracketsIPv6Hosts()
    {
        var device = new SonosDevice("Kitchen", IPAddress.Parse("fe80::1"), 1400, "uuid:RINCON_1");
        device.GroupRenderingControlUrl
            .Should().Be("http://[fe80::1]:1400/MediaRenderer/GroupRenderingControl/Control");
    }

    [Fact]
    public void SetGroupVolumeEnvelope_UsesGroupServiceAndHasNoChannelArgument()
    {
        var xml = SonosController.BuildSetGroupVolumeEnvelope(42);

        xml.Should().Contain("urn:schemas-upnp-org:service:GroupRenderingControl:1");
        xml.Should().Contain("<u:SetGroupVolume");
        xml.Should().Contain("<DesiredVolume>42</DesiredVolume>");
        xml.Should().Contain("<InstanceID>0</InstanceID>");
        // GroupRenderingControl has no Channel argument; sending one faults.
        xml.Should().NotContain("<Channel>");
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(140, 100)]
    public void SetGroupVolumeEnvelope_ClampsToValidRange(int input, int expected)
    {
        SonosController.BuildSetGroupVolumeEnvelope(input)
            .Should().Contain($"<DesiredVolume>{expected}</DesiredVolume>");
    }

    [Fact]
    public void GetGroupVolumeEnvelope_UsesGroupService()
    {
        var xml = SonosController.BuildGetGroupVolumeEnvelope();

        xml.Should().Contain("urn:schemas-upnp-org:service:GroupRenderingControl:1");
        xml.Should().Contain("<u:GetGroupVolume");
        xml.Should().NotContain("<Channel>");
    }

    [Fact]
    public void SnapshotGroupVolumeEnvelope_UsesGroupService()
    {
        var xml = SonosController.BuildSnapshotGroupVolumeEnvelope();

        xml.Should().Contain("urn:schemas-upnp-org:service:GroupRenderingControl:1");
        xml.Should().Contain("<u:SnapshotGroupVolume");
    }

    [Fact]
    public void PerPlayerEnvelopes_StillTargetRenderingControl()
    {
        // The fallback path for devices without GroupRenderingControl.
        SonosController.BuildSetVolumeEnvelope(30)
            .Should().Contain("urn:schemas-upnp-org:service:RenderingControl:1")
            .And.Contain("<Channel>Master</Channel>");
    }
}
