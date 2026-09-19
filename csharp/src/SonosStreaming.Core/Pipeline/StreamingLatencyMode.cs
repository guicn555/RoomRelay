namespace SonosStreaming.Core.Pipeline;

public enum StreamingLatencyMode
{
    Stable = 0,
    LowLatency = 1,
}

public static class StreamingLatencyModeExtensions
{
    public static string DisplayName(this StreamingLatencyMode mode) => mode switch
    {
        StreamingLatencyMode.LowLatency => "Low latency",
        _ => "Stable",
    };

    public static int CaptureBufferMs(this StreamingLatencyMode mode) => mode switch
    {
        StreamingLatencyMode.LowLatency => 50,
        _ => 200,
    };

    public static int PcmFlushBytes(this StreamingLatencyMode mode) => mode switch
    {
        StreamingLatencyMode.LowLatency => 4096,
        _ => 8192,
    };

    /// <summary>
    /// How long the pump waits for a capture buffer before treating the source
    /// as idle and letting the rate governor pad the gap.
    ///
    /// This must exceed the longest legitimate gap between buffers. If it
    /// doesn't, a buffer that is merely late trips the timeout, the governor
    /// pads for audio it assumes is missing, and then the real buffer arrives
    /// and is encoded too. The stream is then permanently ahead of real time by
    /// the padded amount, one step per gap. A fixed 100 ms was shorter than
    /// Stable mode's own 200 ms capture buffer, so it fired routinely.
    /// </summary>
    public static int StallTimeoutMs(this StreamingLatencyMode mode) =>
        Math.Max(300, mode.CaptureBufferMs() * 2);
}
