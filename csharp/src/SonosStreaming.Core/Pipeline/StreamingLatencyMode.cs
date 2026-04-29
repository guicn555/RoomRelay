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
}
