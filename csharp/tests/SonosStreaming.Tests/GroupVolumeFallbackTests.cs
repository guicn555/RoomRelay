using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using SonosStreaming.Core.Network;
using Xunit;

namespace SonosStreaming.Tests;

// Issue #30: GroupRenderingControl is not advertised in device_description.xml
// and bonded satellites reject it (a real Sonos Sub answers HTTP 500), so
// SonosController has to fall back to per-player RenderingControl.
public sealed class GroupVolumeFallbackTests : IDisposable
{
    private const string GroupPath = "/MediaRenderer/GroupRenderingControl/Control";
    private const string PlayerPath = "/MediaRenderer/RenderingControl/Control";

    private readonly TcpListener _listener;
    private readonly ushort _port;
    private readonly ConcurrentQueue<string> _requests = new();

    /// <summary>When true the mock rejects GroupRenderingControl like a Sub does.</summary>
    private volatile bool _rejectGroupService;

    public GroupVolumeFallbackTests()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public void Dispose() => _listener.Stop();

    // Each test uses a fresh UDN: SonosController caches rejections in a static
    // set keyed by UDN so repeated slider moves don't re-pay a failing round
    // trip, and that cache would otherwise leak between tests.
    private SonosDevice Device(string udn) =>
        new("Kitchen + Den", IPAddress.Loopback, _port, udn);

    private string[] Requests => _requests.ToArray();

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { break; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using var _ = client;
            using var ns = client.GetStream();
            using var reader = new StreamReader(ns, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(ns, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

            var requestLine = await reader.ReadLineAsync();
            if (requestLine == null) return;
            var path = requestLine.Split(' ') is { Length: >= 2 } parts ? parts[1] : "";

            int contentLength = 0;
            while (true)
            {
                var header = await reader.ReadLineAsync();
                if (header == null || header.Length == 0) break;
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(header[15..].Trim(), out contentLength);
            }
            if (contentLength > 0)
            {
                var buf = new char[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int n = await reader.ReadAsync(buf, read, contentLength - read);
                    if (n <= 0) break;
                    read += n;
                }
            }

            _requests.Enqueue(path);

            if (path == GroupPath && _rejectGroupService)
            {
                await writer.WriteAsync("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n");
                return;
            }

            var body = path == GroupPath
                ? "<?xml version=\"1.0\"?><s:Envelope><s:Body><u:GetGroupVolumeResponse><CurrentVolume>21</CurrentVolume></u:GetGroupVolumeResponse></s:Body></s:Envelope>"
                : "<?xml version=\"1.0\"?><s:Envelope><s:Body><u:GetVolumeResponse><CurrentVolume>37</CurrentVolume></u:GetVolumeResponse></s:Body></s:Envelope>";
            await writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
        }
        catch { }
    }

    [Fact]
    public async Task GetVolume_PrefersTheGroupService()
    {
        var ct = TestContext.Current.CancellationToken;

        var volume = await new SonosController().GetVolumeAsync(Device("uuid:RINCON_GETGROUP"), ct);

        volume.Should().Be(21);
        Requests.Should().ContainSingle().Which.Should().Be(GroupPath);
    }

    [Fact]
    public async Task SetVolume_PrefersTheGroupService()
    {
        var ct = TestContext.Current.CancellationToken;

        await new SonosController().SetVolumeAsync(Device("uuid:RINCON_SETGROUP"), 40, ct);

        Requests.Should().ContainSingle().Which.Should().Be(GroupPath);
    }

    [Fact]
    public async Task GetVolume_FallsBackToThePlayerWhenTheGroupServiceRejects()
    {
        var ct = TestContext.Current.CancellationToken;
        _rejectGroupService = true;

        var volume = await new SonosController().GetVolumeAsync(Device("uuid:RINCON_GETFALLBACK"), ct);

        volume.Should().Be(37);
        Requests.Should().Equal(GroupPath, PlayerPath);
    }

    [Fact]
    public async Task SetVolume_FallsBackToThePlayerWhenTheGroupServiceRejects()
    {
        var ct = TestContext.Current.CancellationToken;
        _rejectGroupService = true;

        await new SonosController().SetVolumeAsync(Device("uuid:RINCON_SETFALLBACK"), 40, ct);

        Requests.Should().Equal(GroupPath, PlayerPath);
    }

    [Fact]
    public async Task ARejectionIsRememberedSoLaterCallsSkipTheGroupService()
    {
        // Every slider move builds a new SonosController, so the cache has to
        // outlive the instance or a Sub would pay a failing round trip per move.
        var ct = TestContext.Current.CancellationToken;
        _rejectGroupService = true;
        var device = Device("uuid:RINCON_REMEMBERED");

        await new SonosController().SetVolumeAsync(device, 10, ct);
        await new SonosController().SetVolumeAsync(device, 20, ct);
        await new SonosController().SetVolumeAsync(device, 30, ct);

        // Group service tried once, then skipped for every later call.
        Requests.Should().Equal(GroupPath, PlayerPath, PlayerPath, PlayerPath);
    }

    [Fact]
    public async Task SnapshotGroupVolume_IsBestEffortAndDoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        _rejectGroupService = true;

        var act = async () => await new SonosController()
            .SnapshotGroupVolumeAsync(Device("uuid:RINCON_SNAPSHOT"), ct);

        await act.Should().NotThrowAsync();
    }
}
