using System.Net;

namespace SonosStreaming.Core.Network;

public interface ISonosController
{
    Task SetUriAndPlayAsync(SonosDevice device, string streamUrl, CancellationToken ct = default);
    Task StopAsync(SonosDevice device, CancellationToken ct = default);
}
