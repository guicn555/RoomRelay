namespace SonosStreaming.Core.Pipeline;

/// <summary>
/// Holds the encoded stream to real time.
///
/// Nothing else in the pipeline paces the output: the encoder emits exactly
/// what capture delivers, so anything that loses capture buffers (a pump
/// hiccup, a device stall, an idle gap) permanently shortens the stream. AAC
/// tolerates that because ADTS frames are self-describing and Sonos resyncs,
/// but raw PCM has no framing, so the speaker's jitter buffer drains and
/// playback stalls after a minute or so (issue #26).
///
/// The governor compares sample frames produced against sample frames the wall
/// clock says should exist, pads the shortfall with silence, and skips a buffer
/// when output has run far enough ahead that the lead would become latency.
/// </summary>
public sealed class OutputRateGovernor
{
    private readonly uint _rate;
    private readonly Func<DateTime> _clock;
    private readonly long _padThresholdFrames;
    private readonly long _maxPadPerCallFrames;
    private readonly long _trimThresholdFrames;

    private DateTime _start;
    private long _produced;
    private long _padded;
    private long _trimmed;

    /// <param name="rate">Output sample rate, in frames per second.</param>
    /// <param name="clock">Wall clock; injectable so tests can drive it.</param>
    /// <param name="padThresholdMs">
    /// Shortfall that must build up before silence is inserted. This is a
    /// deadband, and it wants to be wider than the jitter the capture thread
    /// can introduce. At 20 ms it fired on ordinary scheduling noise, and every
    /// pad is a chance to insert silence for audio that was merely late and
    /// then arrives anyway, which leaves the stream ahead of the clock until
    /// the trim drops a buffer of real audio.
    /// </param>
    /// <param name="maxPadPerCallMs">Ceiling on silence inserted by a single call.</param>
    /// <param name="trimThresholdMs">Lead over real time at which a buffer is skipped.</param>
    public OutputRateGovernor(
        uint rate,
        Func<DateTime>? clock = null,
        double padThresholdMs = 150,
        double maxPadPerCallMs = 1000,
        double trimThresholdMs = 250)
    {
        if (rate == 0) throw new ArgumentOutOfRangeException(nameof(rate));
        _rate = rate;
        _clock = clock ?? (() => DateTime.UtcNow);
        _padThresholdFrames = MsToFrames(padThresholdMs);
        _maxPadPerCallFrames = Math.Max(1, MsToFrames(maxPadPerCallMs));
        _trimThresholdFrames = MsToFrames(trimThresholdMs);
    }

    /// <summary>True once <see cref="Start"/> has anchored the clock.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Sample frames handed to the encoder, including inserted silence.</summary>
    public long ProducedFrames => _produced;

    /// <summary>Silence inserted to hold the output to real time.</summary>
    public long PaddedFrames => _padded;

    /// <summary>Audio skipped because output ran ahead of real time.</summary>
    public long TrimmedFrames => _trimmed;

    public double PaddedMs => FramesToMs(_padded);
    public double TrimmedMs => FramesToMs(_trimmed);

    /// <summary>Positive means the stream is behind real time, negative means ahead.</summary>
    public double SkewMs => IsRunning ? FramesToMs(SkewFrames()) : 0;

    /// <summary>
    /// Anchors the clock. Call this when the stream starts, not on the first
    /// captured frame: WASAPI loopback delivers nothing at all while the
    /// endpoint has no active stream, so a governor that waited for real audio
    /// would never pad and a silent PC would send Sonos an empty body.
    /// </summary>
    public void Start()
    {
        _start = _clock();
        IsRunning = true;
        _produced = 0;
        _padded = 0;
        _trimmed = 0;
    }

    /// <summary>
    /// Freezes the clock while keeping the counters, so teardown does not go on
    /// accumulating skew against a capture source that has already been
    /// disposed. Without this the last diagnostics of a session report many
    /// seconds of phantom debt and read like a starving stream.
    /// </summary>
    public void Stop() => IsRunning = false;

    public void Reset()
    {
        IsRunning = false;
        _produced = 0;
        _padded = 0;
        _trimmed = 0;
    }

    /// <summary>
    /// Records that a decoded buffer is about to be encoded. Returns false when
    /// output has run far enough ahead that the buffer should be skipped
    /// instead; skipping leaves the produced count alone, so the lead shrinks
    /// on its own as the clock advances.
    /// </summary>
    public bool Accept(int frameCount)
    {
        if (frameCount <= 0 || !IsRunning) return false;

        if (SkewFrames() <= -_trimThresholdFrames)
        {
            _trimmed += frameCount;
            return false;
        }

        _produced += frameCount;
        return true;
    }

    /// <summary>
    /// Returns the number of silence frames the caller should encode to catch
    /// up with the wall clock, or zero while the shortfall is within tolerance.
    /// </summary>
    public int PadFrames()
    {
        if (!IsRunning) return 0;

        long deficit = SkewFrames();
        if (deficit < _padThresholdFrames) return 0;

        int pad = (int)Math.Min(deficit, _maxPadPerCallFrames);
        _produced += pad;
        _padded += pad;
        return pad;
    }

    private long SkewFrames()
    {
        long expected = (long)((_clock() - _start).TotalSeconds * _rate);
        return expected - _produced;
    }

    private long MsToFrames(double ms) => (long)(ms * _rate / 1000.0);
    private double FramesToMs(long frames) => frames * 1000.0 / _rate;
}
