namespace SonosStreaming.Core.Pipeline;

public sealed class PipelineOptions
{
    public int HttpPort { get; init; } = 8000;
    public int BroadcastCapacity { get; init; } = 64;
    public int CaptureChannelCapacity { get; init; } = 8;
    public int SsdpTimeoutMs { get; init; } = 3000;
}
