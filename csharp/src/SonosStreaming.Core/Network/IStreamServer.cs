using System.Net;

namespace SonosStreaming.Core.Network;

public interface IStreamServer : IDisposable
{
    void Start();
    Task ShutdownAsync();
    string StreamUrl(string? hostOverride = null);
    IPEndPoint LocalEndPoint { get; }
}
