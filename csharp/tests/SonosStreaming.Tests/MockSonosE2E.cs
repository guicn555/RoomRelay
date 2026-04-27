using SonosStreaming.Core.Network;
using SonosStreaming.Core.Audio;
using FluentAssertions;
using Xunit;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SonosStreaming.Tests;

public class MockSonosE2E : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _port;

    public MockSonosE2E()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public void Dispose()
    {
        _listener.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = HandleClientAsync(client);
            }
            catch { break; }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var ns = client.GetStream();
        using var reader = new StreamReader(ns, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(ns, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        var requestLine = await reader.ReadLineAsync();
        if (requestLine == null) return;

        var headers = new List<string>();
        while (true)
        {
            var header = await reader.ReadLineAsync();
            if (header == null || header.Length == 0) break;
            headers.Add(header);
        }

        var responseBody = headers.Any(h => h.Contains("GetVolume", StringComparison.OrdinalIgnoreCase))
            ? "<?xml version=\"1.0\"?><s:Envelope><s:Body><u:GetVolumeResponse><CurrentVolume>37</CurrentVolume></u:GetVolumeResponse></s:Body></s:Envelope>"
            : "";
        var responseBodyBytes = Encoding.UTF8.GetByteCount(responseBody);
        await writer.WriteAsync($"HTTP/1.1 200 OK\r\nContent-Length: {responseBodyBytes}\r\n\r\n{responseBody}");
        client.Close();
    }

    [Fact]
    public void SonosController_SendsCorrectSoapSequence()
    {
        var device = new SonosDevice("Test", IPAddress.Loopback, (ushort)_port, "uuid:TEST");
        var setUriEnv = SonosController.BuildSetUriEnvelope("192.168.1.10:8000/stream.aac");
        var playEnv = SonosController.BuildPlayEnvelope();
        var stopEnv = SonosController.BuildStopEnvelope();

        setUriEnv.Should().Contain("x-rincon-mp3radio://192.168.1.10:8000/stream.aac");
        setUriEnv.Should().Contain("<InstanceID>0</InstanceID>");
        playEnv.Should().Contain("<Speed>1</Speed>");
        stopEnv.Should().Contain("<u:Stop");
    }

    [Fact]
    public void BuildSetUriEnvelope_WithoutTitle_HasEmptyMetadata()
    {
        var env = SonosController.BuildSetUriEnvelope("192.168.1.10:8000/stream.aac");
        env.Should().Contain("<CurrentURIMetaData></CurrentURIMetaData>");
    }

    [Fact]
    public void BuildSetUriEnvelope_WithTitle_ContainsDidlLiteMetadata()
    {
        var env = SonosController.BuildSetUriEnvelope("192.168.1.10:8000/stream.aac", useRadioScheme: true, metadataTitle: "RoomRelay — Living Room");
        env.Should().Contain("dc:title");
        env.Should().Contain("RoomRelay — Living Room");
        env.Should().Contain("audioBroadcast");
        env.Should().Contain("DIDL-Lite");
    }

    [Fact]
    public void SonosController_LpcmSetUri_UsesPlainHttpUri()
    {
        var setUriEnv = SonosController.BuildSetUriEnvelope("http://192.168.1.10:8000/stream/test.wav", useRadioScheme: false);

        setUriEnv.Should().Contain("<CurrentURI>http://192.168.1.10:8000/stream/test.wav</CurrentURI>");
        setUriEnv.Should().NotContain("x-rincon-mp3radio://");
    }

    [Fact]
    public void SonosController_RenderingControlVolumeEnvelopes_AreValid()
    {
        var get = SonosController.BuildGetVolumeEnvelope();
        var set = SonosController.BuildSetVolumeEnvelope(125);

        get.Should().Contain("<u:GetVolume");
        get.Should().Contain("<Channel>Master</Channel>");
        set.Should().Contain("<u:SetVolume");
        set.Should().Contain("<DesiredVolume>100</DesiredVolume>");
    }

    [Fact]
    public async Task SonosController_GetVolumeAsync_ReadsCurrentVolume()
    {
        var device = new SonosDevice("Test", IPAddress.Loopback, (ushort)_port, "uuid:TEST");

        var volume = await new SonosController().GetVolumeAsync(device);

        volume.Should().Be(37);
    }

    [Fact]
    public void StripScheme_DropsHttp()
    {
        SonosController.StripScheme("http://host:8000/a").Should().Be("host:8000/a");
        SonosController.StripScheme("https://host/a").Should().Be("host/a");
        SonosController.StripScheme("host/a").Should().Be("host/a");
    }
}
