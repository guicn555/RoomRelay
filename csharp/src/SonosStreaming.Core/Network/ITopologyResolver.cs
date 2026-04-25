namespace SonosStreaming.Core.Network;

public interface ITopologyResolver
{
    Task<List<SonosDevice>> ResolveCoordinatorsAsync(List<SonosDevice> devices, CancellationToken ct = default);
}
