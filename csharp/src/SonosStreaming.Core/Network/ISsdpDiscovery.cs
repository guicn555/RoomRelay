using System.Net;

namespace SonosStreaming.Core.Network;

public interface ISsdpDiscovery : IDisposable
{
    Task<List<SonosDevice>> ScanAsync(int timeoutMs = 3000, CancellationToken ct = default);
    Task<SonosDevice> LookupAsync(IPAddress ip, ushort port = 1400, CancellationToken ct = default);
}
