using System.Net;
using SonosStreaming.Core.Audio;
using SonosStreaming.Core.Network;
using FluentAssertions;
using Xunit;

namespace SonosStreaming.Tests;

public class StreamServerTests
{
    [Fact]
    public void StreamUrl_WithHostOverride_FormatsCorrectly()
    {
        var broadcast = new BroadcastChannel<ReadOnlyMemory<byte>>();
        var server = new StreamServer(broadcast, 0);
        server.Start();
        try
        {
            var url = server.StreamUrl("192.168.1.10:8000");
            url.Should().MatchRegex(@"^http://192\.168\.1\.10:8000/stream/[0-9a-f]{16}\.aac$");
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public void StreamUrl_WithoutOverride_UsesLocalEndPoint()
    {
        var broadcast = new BroadcastChannel<ReadOnlyMemory<byte>>();
        var server = new StreamServer(broadcast, 0);
        server.Start();
        try
        {
            var url = server.StreamUrl();
            var port = server.LocalEndPoint.Port;
            port.Should().BeGreaterThan(0);
            url.Should().Be($"http://{server.LocalEndPoint}{server.StreamPath}");
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public void StreamUrl_LpcmFormat_UsesWavExtension()
    {
        var broadcast = new BroadcastChannel<ReadOnlyMemory<byte>>();
        var server = new StreamServer(broadcast, 0, StreamingFormat.Lpcm);
        server.Start();
        try
        {
            var url = server.StreamUrl("192.168.1.10:8000");
            url.Should().MatchRegex(@"^http://192\.168\.1\.10:8000/stream/[0-9a-f]{16}\.wav$");
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public void Start_SetsLocalEndPoint()
    {
        var broadcast = new BroadcastChannel<ReadOnlyMemory<byte>>();
        var server = new StreamServer(broadcast, 0);
        server.LocalEndPoint.Port.Should().Be(0);
        server.Start();
        try
        {
            server.LocalEndPoint.Port.Should().BeGreaterThan(0);
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public void Start_WithBindAddress_UsesRequestedInterface()
    {
        var broadcast = new BroadcastChannel<ReadOnlyMemory<byte>>();
        var bindAddress = IPAddress.Loopback;
        var server = new StreamServer(broadcast, 0, bindAddress: bindAddress);
        server.Start();
        try
        {
            server.LocalEndPoint.Address.Should().Be(bindAddress);
            server.LocalEndPoint.Port.Should().BeGreaterThan(0);
        }
        finally
        {
            server.Dispose();
        }
    }
}
