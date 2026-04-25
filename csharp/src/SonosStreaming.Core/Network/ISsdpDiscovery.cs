namespace SonosStreaming.Core.Network;

public interface ISsdpDiscovery : IDisposable
{
    Task<List<SonosDevice>> ScanAsync(int timeoutMs = 3000, CancellationToken ct = default);
}
