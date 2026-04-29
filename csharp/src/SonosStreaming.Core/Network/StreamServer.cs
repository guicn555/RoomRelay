using System.Net;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Serilog;
using SonosStreaming.Core.Audio;

namespace SonosStreaming.Core.Network;

public sealed class StreamServer : IStreamServer
{
    private readonly TcpListener _listener;
    private readonly BroadcastChannel<ReadOnlyMemory<byte>> _broadcast;
    private readonly StreamingFormat _format;
    private readonly string _streamToken;
    private CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private readonly List<Task> _connectionTasks = new();
    private readonly object _lock = new();
    private IPEndPoint _localEndPoint;
    private int _slowWriteCount;

    // 44-byte RIFF/WAVE header for 48 kHz stereo 16-bit PCM streaming.
    private static readonly byte[] LpcmWavHeader = GenerateWavHeader();

    public IPEndPoint LocalEndPoint => _localEndPoint;
    public string StreamPath => $"/stream/{_streamToken}{_format.FileExtension()}";
    public int SlowWriteCount => Volatile.Read(ref _slowWriteCount);

    public StreamServer(BroadcastChannel<ReadOnlyMemory<byte>> broadcast, int port = 8000, StreamingFormat format = StreamingFormat.Aac256, IPAddress? bindAddress = null)
    {
        _broadcast = broadcast;
        _format = format;
        _streamToken = RandomNumberGenerator.GetHexString(16).ToLowerInvariant();
        var address = bindAddress ?? IPAddress.Any;
        _listener = new TcpListener(address, port);
        _localEndPoint = new IPEndPoint(address, port);
    }

    public string StreamUrl(string? hostOverride = null)
    {
        string host = hostOverride ?? _localEndPoint.ToString();
        return $"http://{host}{StreamPath}";
    }

    private static byte[] GenerateWavHeader()
    {
        var h = new byte[44];
        int o = 0;
        void Ascii(string s) { foreach (char c in s) h[o++] = (byte)c; }
        void U16(ushort v) { h[o++] = (byte)(v & 0xFF); h[o++] = (byte)(v >> 8); }
        void U32(uint v)
        {
            h[o++] = (byte)(v & 0xFF);
            h[o++] = (byte)((v >> 8) & 0xFF);
            h[o++] = (byte)((v >> 16) & 0xFF);
            h[o++] = (byte)(v >> 24);
        }
        Ascii("RIFF");
        U32(0xFFFFFFFF);          // chunk size (unknown / streaming)
        Ascii("WAVE");
        Ascii("fmt ");
        U32(16);                  // subchunk size
        U16(1);                   // PCM format
        U16(2);                   // channels
        U32(48000);               // sample rate
        U32(48000u * 4);          // byte rate
        U16(4);                   // block align
        U16(16);                  // bits per sample
        Ascii("data");
        U32(0xFFFFFFFF);          // data chunk size (unknown / streaming)
        return h;
    }

    public void Start()
    {
        _listener.Start();
        _localEndPoint = (IPEndPoint)_listener.LocalEndpoint;
        Log.Information("HTTP stream server listening on {Addr}", _localEndPoint);

        _acceptTask = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                tcp.NoDelay = true;
                // LPCM (~1.5 Mbps) needs a much larger send buffer than AAC to avoid
                // TCP backpressure causing audio dropouts.
                tcp.SendBufferSize = _format.IsPcm() ? 65536 : 8192;
                var clientTask = ServeClientAsync(tcp);
                lock (_lock) { _connectionTasks.Add(clientTask); }
                _ = clientTask.ContinueWith(_ =>
                {
                    lock (_lock) { _connectionTasks.Remove(clientTask); }
                }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Accept loop error");
        }
    }

    private async Task ServeClientAsync(TcpClient tcp)
    {
        var peer = tcp.Client.RemoteEndPoint;
        Log.Information("Stream connection from {Peer}", peer);

        try
        {
            using var ns = tcp.GetStream();
            using var reader = new StreamReader(ns, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
            if (requestLine == null) return;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2 || parts[1] != StreamPath)
            {
                await WriteResponseAsync(ns, "HTTP/1.0 404 Not Found\r\nConnection: close\r\n\r\n").ConfigureAwait(false);
                return;
            }

            var requestHeaders = new List<string>();
            while (true)
            {
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line == null || line.Length == 0) break;
                requestHeaders.Add(line);
            }
            Log.Debug("Client {Peer} headers: {Headers}", peer, string.Join(" | ", requestHeaders));

            if (_format.IsPcm() && requestHeaders.Any(h => h.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)))
            {
                // Sonos probes WAV/LPCM URLs with byte ranges as if they were seekable files.
                // RoomRelay streams live audio only, so reject probes instead of creating a
                // subscriber that will never consume the continuous stream correctly.
                await WriteResponseAsync(ns, "HTTP/1.0 416 Range Not Satisfiable\r\nConnection: close\r\nAccept-Ranges: none\r\nContent-Range: bytes */*\r\n\r\n").ConfigureAwait(false);
                Log.Information("Rejected PCM range request from {Peer}", peer);
                return;
            }

            // ICY (Shoutcast) headers only make sense for compressed audio streams.
            // Sending them with PCM causes Sonos to enter ICY/MP3 decode mode.
            var icyHeaders = _format.IsPcm() ? "" :
                "icy-name: RoomRelay (Windows)\r\nicy-pub: 0\r\n";
            var pcmHeaders = _format.IsPcm() ? PcmStreamingHeaders(_format) : "";
            var headerStr = "HTTP/1.0 200 OK\r\n" +
                            $"Content-Type: {_format.ContentType()}\r\n" +
                            "Connection: close\r\n" +
                            "Cache-Control: no-cache, no-store\r\n" +
                            "Accept-Ranges: none\r\n" +
                            pcmHeaders +
                            icyHeaders + "\r\n";
            var header = Encoding.ASCII.GetBytes(headerStr);
            await ns.WriteAsync(header, 0, header.Length, _cts.Token).ConfigureAwait(false);

            // Every LPCM connection gets its own WAV container header so
            // late-joining or reconnecting clients always start with valid RIFF/WAVE.
            if (_format == StreamingFormat.WavPcm)
            {
                await ns.WriteAsync(LpcmWavHeader, 0, LpcmWavHeader.Length, _cts.Token).ConfigureAwait(false);
            }

            var subscription = _broadcast.Subscribe();
            long bytes = 0;
            int frames = 0;
            var slowWrites = 0;
            var t0 = DateTime.UtcNow;
            try
            {
                await foreach (var frame in subscription.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    var writeStart = DateTime.UtcNow;
                    await ns.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
                    var writeMs = (DateTime.UtcNow - writeStart).TotalMilliseconds;
                    if (writeMs > 250)
                    {
                        slowWrites++;
                        Interlocked.Increment(ref _slowWriteCount);
                        Log.Warning("Slow stream write to {Peer}: {Bytes} bytes in {Ms:F0} ms (format={Format})",
                            peer, frame.Length, writeMs, _format);
                    }
                    bytes += frame.Length;
                    frames++;
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
            catch (IOException ex) { Log.Information("Client {Peer} IO ended: {Msg}", peer, ex.Message); }
            catch (SocketException ex) { Log.Information("Client {Peer} socket ended: {Msg}", peer, ex.Message); }
            var elapsed = (DateTime.UtcNow - t0).TotalSeconds;
            Log.Information("Client {Peer} closed: {Frames} frames, {Bytes} bytes, {Elapsed:F1}s ({Kbps:F0} kbps), slowWrites={SlowWrites}, format={Format}",
                peer, frames, bytes, elapsed, elapsed > 0 ? bytes * 8 / 1000 / elapsed : 0, slowWrites, _format);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Stream connection to {Peer} ended", peer);
        }
        finally
        {
            tcp.Close();
        }
    }

    private static async Task WriteResponseAsync(NetworkStream ns, string response)
    {
        var bytes = Encoding.ASCII.GetBytes(response);
        await ns.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
    }

    private static string PcmStreamingHeaders(StreamingFormat format)
    {
        var profile = format == StreamingFormat.WavPcm ? "WAV" : "LPCM";
        return "transferMode.dlna.org: Streaming\r\n" +
               $"contentFeatures.dlna.org: DLNA.ORG_PN={profile};DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000\r\n";
    }

    public async Task ShutdownAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _broadcast.CompleteAll();

        if (_acceptTask != null)
        {
            try { await _acceptTask.ConfigureAwait(false); } catch { }
        }

        Task[] remaining;
        lock (_lock) { remaining = _connectionTasks.ToArray(); }
        try { await Task.WhenAll(remaining).ConfigureAwait(false); } catch { }

        Log.Information("Stream server shut down");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _broadcast.CompleteAll();
        _cts.Dispose();
    }
}
