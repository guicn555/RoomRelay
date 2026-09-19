using System.Net;
using System.Net.Sockets;
using System.Text;
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
    public void StreamUrl_WithIPv6HostOverride_FormatsCorrectly()
    {
        var broadcast = new BroadcastChannel<ReadOnlyMemory<byte>>();
        var server = new StreamServer(broadcast, 0);
        server.Start();
        try
        {
            var url = server.StreamUrl("[fe80::1]:8000");
            url.Should().MatchRegex(@"^http://\[fe80::1\]:8000/stream/[0-9a-f]{16}\.aac$");
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
    public void StreamUrl_WavPcmFormat_UsesWavExtension()
    {
        var broadcast = new BroadcastChannel<ReadOnlyMemory<byte>>();
        var server = new StreamServer(broadcast, 0, StreamingFormat.WavPcm);
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
    public void StreamUrl_RetiredL16PcmFormat_FallsBackToWavExtension()
    {
        // L16Pcm is retired (issue #32) but survives in the enum so old
        // settings deserialize. A stale value must serve WAV, which Sonos
        // supports, rather than audio/L16, which it does not.
        var broadcast = new BroadcastChannel<ReadOnlyMemory<byte>>();
        var server = new StreamServer(broadcast, 0, StreamingFormat.L16Pcm);
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

    [Fact]
    public async Task PcmRangeRequest_ReturnsRangeNotSatisfiable()
    {
        var ct = TestContext.Current.CancellationToken;
        var broadcast = new BroadcastChannel<ReadOnlyMemory<byte>>();
        var server = new StreamServer(broadcast, 0, StreamingFormat.WavPcm, IPAddress.Loopback);
        server.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.LocalEndPoint.Port, ct);
            await using var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes($"GET {server.StreamPath} HTTP/1.0\r\nHost: localhost\r\nRange: bytes=1024-\r\n\r\n");
            await stream.WriteAsync(request, ct);

            var buffer = new byte[512];
            var read = await stream.ReadAsync(buffer, ct);
            var response = Encoding.ASCII.GetString(buffer, 0, read);

            response.Should().StartWith("HTTP/1.0 416 Range Not Satisfiable");
            response.Should().Contain("Accept-Ranges: none");
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task DisconnectedClient_IsUnsubscribedFromBroadcast()
    {
        // A dead connection used to stay registered until its buffer filled,
        // inflating the client count and eventually logging a bogus "slow
        // subscriber" warning (issue #26).
        var ct = TestContext.Current.CancellationToken;
        var broadcast = new BroadcastChannel<ReadOnlyMemory<byte>>();
        var server = new StreamServer(broadcast, 0, StreamingFormat.Aac256, IPAddress.Loopback);
        server.Start();
        try
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, server.LocalEndPoint.Port, ct);
                await using var stream = client.GetStream();
                var request = Encoding.ASCII.GetBytes($"GET {server.StreamPath} HTTP/1.0\r\nHost: localhost\r\n\r\n");
                await stream.WriteAsync(request, ct);

                await WaitForSubscriberCountAsync(broadcast, 1, ct);
                broadcast.SubscriberCount.Should().Be(1);
            }

            // Writes are what surface the broken socket to the serve loop.
            var payload = new ReadOnlyMemory<byte>(new byte[4096]);
            for (int i = 0; i < 200 && broadcast.SubscriberCount > 0; i++)
            {
                broadcast.Write(payload);
                await Task.Delay(10, ct);
            }

            broadcast.SubscriberCount.Should().Be(0);
            broadcast.DroppedSubscribers.Should().Be(0);
        }
        finally
        {
            server.Dispose();
        }
    }

    private static async Task WaitForSubscriberCountAsync<T>(BroadcastChannel<T> broadcast, int expected, CancellationToken ct)
    {
        for (int i = 0; i < 200 && broadcast.SubscriberCount != expected; i++)
            await Task.Delay(10, ct);
    }
}
