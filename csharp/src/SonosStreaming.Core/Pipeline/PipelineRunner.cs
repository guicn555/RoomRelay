using SonosStreaming.Core.Audio;
using SonosStreaming.Core.Audio.Dsp;
using SonosStreaming.Core.Network;
using SonosStreaming.Core.State;
using Serilog;
using System.Net;

namespace SonosStreaming.Core.Pipeline;

public sealed class PipelineRunner : IDisposable
{
    private readonly AppCore _core;
    private readonly PipelineOptions _options;
    private readonly GainStage _gainStage;
    private readonly BalanceStage _balanceStage;
    private readonly VolumeStage _volumeStage;
    private readonly BiquadEqualizer _equalizer;
    private readonly ChannelDelay _channelDelay;
    private readonly VuMeter _vuMeter;
    private readonly SpectrumAnalyzer _spectrumAnalyzer;
    private readonly BroadcastChannel<ReadOnlyMemory<byte>> _broadcast;

    public StreamingFormat Format { get; set; } = StreamingFormat.Aac256;
    public StreamingLatencyMode LatencyMode { get; set; } = StreamingLatencyMode.Stable;

    private StreamServer? _streamServer;
    private IAudioSource? _audioSource;
    private Resampler? _resampler;
    private IAudioEncoder? _encoder;
    private EndpointMuteGuard? _muteGuard;
    private AudioEndpointMonitor? _endpointMonitor;
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private Task? _clientCountTask;
    private long _framesEmitted;
    private int _lastSlowWriteCount;
    private DateTime _pipelineStart;
    private DateTime _lastFrameLog;
    private int _clippingHold;

    public GainStage GainStage => _gainStage;
    public BalanceStage BalanceStage => _balanceStage;
    public VolumeStage VolumeStage => _volumeStage;
    public BiquadEqualizer Equalizer => _equalizer;
    public ChannelDelay ChannelDelay => _channelDelay;
    public VuMeter VuMeter => _vuMeter;
    public SpectrumAnalyzer SpectrumAnalyzer => _spectrumAnalyzer;
    public BroadcastChannel<ReadOnlyMemory<byte>> Broadcast => _broadcast;
    public MixFormat? CurrentMixFormat { get; private set; }
    public bool IsClipping { get; private set; }
    public IPAddress? CurrentLocalIp { get; private set; }
    public string? CurrentStreamUrl { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? FirstChunkAtUtc { get; private set; }
    public DateTime? FirstClientAtUtc { get; private set; }
    public long FramesEmitted => Interlocked.Read(ref _framesEmitted);
    public int SlowWriteCount => _streamServer?.SlowWriteCount ?? _lastSlowWriteCount;

    /// <summary>
    /// Raised when the pump loop terminates with an unhandled exception.
    /// </summary>
    public event EventHandler<Exception>? PumpCrashed;

    /// <summary>
    /// Raised when the default audio endpoint format changes while streaming.
    /// </summary>
    public event EventHandler? FormatChanged;

    public PipelineRunner(AppCore core, PipelineOptions? options = null)
    {
        _core = core;
        _options = options ?? new PipelineOptions();
        _gainStage = new GainStage();
        _balanceStage = new BalanceStage();
        _volumeStage = new VolumeStage();
        _equalizer = new BiquadEqualizer();
        _channelDelay = new ChannelDelay();
        _vuMeter = new VuMeter();
        _spectrumAnalyzer = new SpectrumAnalyzer();
        _broadcast = new BroadcastChannel<ReadOnlyMemory<byte>>(_options.BroadcastCapacity);
    }

    public async Task StartAsync(SonosDevice device, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        var selection = _core.Selection;
        if (selection.Source == AudioSourceSelection.Process && selection.ProcessSelection == null)
            throw new InvalidOperationException("Select an application before starting per-application capture.");

        var localIp = LocalIpResolver.PickLocalIpFor(device.Ip);
        CurrentLocalIp = localIp;
        _streamServer = new StreamServer(_broadcast, _options.HttpPort, Format, localIp);
        _streamServer.Start();
        Log.Information("Pipeline latency mode: {LatencyMode}, captureBuffer={CaptureBufferMs} ms, pcmFlushBytes={PcmFlushBytes}",
            LatencyMode, LatencyMode.CaptureBufferMs(), LatencyMode.PcmFlushBytes());

        if (selection.Source == AudioSourceSelection.Process && selection.ProcessSelection != null)
        {
            Log.Information("Starting process loopback for pid={Pid} name={Name} on OS build {Build}",
                selection.ProcessSelection.Pid, selection.ProcessSelection.Name, Environment.OSVersion.Version.Build);
            if (!ProcessLoopbackSource.IsSupported(out var reason))
                throw new InvalidOperationException(reason);

            Log.Information("Creating process loopback source for pid={Pid} name={Name}",
                selection.ProcessSelection.Pid, selection.ProcessSelection.Name);
            try
            {
                var plb = new ProcessLoopbackSource(selection.ProcessSelection.Pid, captureBufferMs: LatencyMode.CaptureBufferMs());
                plb.Start();
                _audioSource = plb;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Could not capture audio from \"{selection.ProcessSelection.Name}\" (pid={selection.ProcessSelection.Pid}). " +
                    $"The application may not be playing audio yet, or it may be protected. " +
                    $"Switch to 'Whole system' capture or try again after the app produces sound.", ex);
            }
        }
        else
        {
            var wsl = new WasapiLoopbackSource(LatencyMode.CaptureBufferMs());
            wsl.Start();
            _audioSource = wsl;
        }

        var mix = _audioSource.MixFormat;
        CurrentMixFormat = mix;
        Log.Information("Capture format: {Rate} Hz, {Ch} ch, {Bits}-bit, float={IsFloat}",
            mix.SampleRate, mix.Channels, mix.BitsPerSample, mix.IsFloat);

        // Media Foundation COM objects are constructed inside the pump task
        // below so creation, use, and release all happen on the same MTA
        // thread. StartAsync is typically invoked on the UI STA thread.

        _framesEmitted = 0;
        _lastSlowWriteCount = 0;
        _pipelineStart = DateTime.UtcNow;
        StartedAtUtc = _pipelineStart;
        FirstChunkAtUtc = null;
        FirstClientAtUtc = null;
        _lastFrameLog = _pipelineStart;
        _pumpTask = Task.Run(() => PumpLoopAsync(token), token);

        _clientCountTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    ClientCount = _broadcast.SubscriberCount;
                    if (ClientCount > 0 && FirstClientAtUtc == null)
                    {
                        FirstClientAtUtc = DateTime.UtcNow;
                        Log.Information("First Sonos client connected after {Ms:F0} ms", (FirstClientAtUtc.Value - _pipelineStart).TotalMilliseconds);
                    }
                }
                catch (OperationCanceledException) { break; }
            }
            ClientCount = 0;
        }, token);

        try
        {
            _muteGuard = new EndpointMuteGuard();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not mute default render endpoint");
        }

        try
        {
            _endpointMonitor = new AudioEndpointMonitor();
            _endpointMonitor.FormatChanged += (_, _) => FormatChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not start endpoint format monitor");
        }

        var port = _streamServer.LocalEndPoint.Port;
        var streamUrl = _streamServer.StreamUrl($"{SsdpDiscovery.FormatHost(localIp)}:{port}");
        CurrentStreamUrl = streamUrl;
        Log.Information("Instructing {Name} to stream from {Url} format={Format} contentType={ContentType} radioScheme={RadioScheme}",
            device.FriendlyName, streamUrl, Format, Format.ContentType(), !Format.IsPcm());

        var sonos = new SonosController();
        await sonos.SetUriAndPlayAsync(device, streamUrl, ct, useRadioScheme: !Format.IsPcm(), contentType: Format.MetadataMimeType(), metadataTitle: BuildMetadataTitle(selection, device)).ConfigureAwait(false);
    }

    public async Task StopAsync(SonosDevice? device)
    {
        var t0 = DateTime.UtcNow;
        _cts?.Cancel();

        // Kick off the Sonos Stop SOAP in parallel with local teardown — it
        // can take seconds on a slow network and there's no reason the local
        // pipeline shutdown should wait for it.
        Task sonosStop = Task.CompletedTask;
        if (device != null)
        {
            sonosStop = Task.Run(async () =>
            {
                try { await new SonosController().StopAsync(device).ConfigureAwait(false); }
                catch (Exception ex) { Log.Warning(ex, "Failed to stop Sonos"); }
            });
        }

        // Stop the capture source first so the pump stops getting new frames,
        // then await the pump so the encoder/resampler aren't disposed while
        // still in use on the pump thread.
        _audioSource?.Dispose();
        _audioSource = null;

        if (_pumpTask != null)
        {
            try { await _pumpTask.ConfigureAwait(false); } catch { }
            _pumpTask = null;
        }

        // The pump owns and disposes Media Foundation objects on its own
        // thread. They should already be null after the awaited pump exit.

        if (_clientCountTask != null)
        {
            try { await _clientCountTask.ConfigureAwait(false); } catch { }
            _clientCountTask = null;
        }

        if (_streamServer != null)
        {
            _lastSlowWriteCount = _streamServer.SlowWriteCount;
            await _streamServer.ShutdownAsync().ConfigureAwait(false);
            _streamServer.Dispose();
            _streamServer = null;
        }

        _muteGuard?.Dispose();
        _muteGuard = null;

        _endpointMonitor?.Dispose();
        _endpointMonitor = null;

        _broadcast.CompleteAll();
        ClientCount = 0;
        CurrentMixFormat = null;
        CurrentLocalIp = null;
        CurrentStreamUrl = null;

        await sonosStop.ConfigureAwait(false);
        Log.Information("Pipeline stopped in {Ms:F0} ms", (DateTime.UtcNow - t0).TotalMilliseconds);
    }

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        Log.Information("Pipeline pump loop starting");
        try
        {
            var mix = CurrentMixFormat ?? throw new InvalidOperationException("Pipeline started without a capture format.");
            _resampler = new Resampler(mix.SampleRate, mix.Channels);
            _encoder = Format switch
            {
                StreamingFormat.Aac128 => new MfAacEncoder(128_000),
                StreamingFormat.Aac192 => new MfAacEncoder(192_000),
                StreamingFormat.Aac256 => new MfAacEncoder(256_000),
                StreamingFormat.Aac320 => new MfAacEncoder(320_000),
                StreamingFormat.WavPcm => (IAudioEncoder)new LpcmEncoder(LatencyMode.PcmFlushBytes()),
                StreamingFormat.L16Pcm => new L16PcmEncoder(LatencyMode.PcmFlushBytes()),
                _                      => new MfAacEncoder(256_000),
            };

            int iter = 0;
            while (!ct.IsCancellationRequested)
            {
                iter++;
                PcmFrameF32? frame;
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(100));
                    frame = await _audioSource!.NextFrameAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    var silenceSamples = (int)(mix.SampleRate / 10) * mix.Channels;
                    frame = PcmFrameF32.Silent(silenceSamples / mix.Channels, mix.SampleRate, mix.Channels);
                }
                catch (OperationCanceledException) { break; }

                if (frame == null) { Log.Warning("Pump got null frame at iter={Iter}, exiting", iter); break; }
                if (iter <= 3)
                    Log.Information("Pump iter {Iter}: frame {Samples} samples @ {Rate} Hz / {Ch} ch", iter, frame.Samples.Length, frame.SampleRate, frame.Channels);

                var samples = frame.Samples.AsSpan();
                _spectrumAnalyzer.Process(samples, frame.Channels);
                _gainStage.Apply(samples, frame.Channels);
                _balanceStage.Apply(samples, frame.Channels);
                _equalizer.Process(samples, frame.Channels);
                _channelDelay.Process(samples, frame.Channels);
                _volumeStage.Apply(samples);
                UpdateClipping(samples);
                _vuMeter.Process(samples, frame.Channels);

                var i16Frame = _resampler.Process(frame);
                _encoder.Encode(i16Frame);
                var chunk = _encoder.FlushChunk();
                if (!chunk.IsEmpty)
                {
                    if (FirstChunkAtUtc == null)
                    {
                        FirstChunkAtUtc = DateTime.UtcNow;
                        Log.Information("First audio chunk emitted after {Ms:F0} ms", (FirstChunkAtUtc.Value - _pipelineStart).TotalMilliseconds);
                    }
                    _broadcast.Write(chunk);
                    Interlocked.Increment(ref _framesEmitted);
                }

                var now = DateTime.UtcNow;
                if ((now - _lastFrameLog).TotalSeconds >= 3)
                {
                    Log.Information("Pipeline: emitted {Frames} encoded frames total ({Rate:F1}/s), subscribers={Subs}, droppedSubscribers={Dropped}",
                        _framesEmitted, _framesEmitted / Math.Max(1.0, (now - _pipelineStart).TotalSeconds), _broadcast.SubscriberCount, _broadcast.DroppedSubscribers);
                    _lastFrameLog = now;
                }
            }

            if (_encoder != null)
            {
                try
                {
                    var drained = _encoder.Drain();
                    if (!drained.IsEmpty)
                        _broadcast.Write(drained);
                }
                catch { }
            }
            Log.Information("Pipeline pump loop exited normally ({Frames} frames emitted)", _framesEmitted);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Pipeline pump loop crashed after {Frames} frames", _framesEmitted);
            PumpCrashed?.Invoke(this, ex);
        }
        finally
        {
            _encoder?.Dispose();
            _encoder = null;
            _resampler?.Dispose();
            _resampler = null;
        }
    }

    public int ClientCount { get; private set; }

    private static string BuildMetadataTitle(Selection selection, SonosDevice device)
    {
        var source = selection.Source == AudioSourceSelection.Process && selection.ProcessSelection != null
            ? selection.ProcessSelection.Name
            : "Whole system";
        return $"RoomRelay - {source} to {device.FriendlyName}";
    }

    private void UpdateClipping(ReadOnlySpan<float> samples)
    {
        var clipped = false;
        for (int i = 0; i < samples.Length; i++)
        {
            if (MathF.Abs(samples[i]) >= 0.999f)
            {
                clipped = true;
                break;
            }
        }

        if (clipped)
            _clippingHold = 30;
        else if (_clippingHold > 0)
            _clippingHold--;

        IsClipping = _clippingHold > 0;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _audioSource?.Dispose();
        // The pump owns Media Foundation objects; disposing them from here
        // can cross COM apartments and crash native MFTs.
        _streamServer?.Dispose();
        _muteGuard?.Dispose();
        _endpointMonitor?.Dispose();
        _cts?.Dispose();
    }
}
