using System.Net;
using System.Net.Sockets;

namespace SonosStreaming.Core.Network;

public static class LocalIpResolver
{
    public static IPAddress PickLocalIpFor(IPAddress target)
    {
        var family = target.AddressFamily;
        using var sock = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        sock.Bind(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        sock.Connect(new IPEndPoint(target, 1400));
        return ((IPEndPoint)sock.LocalEndPoint!).Address;
    }
}
