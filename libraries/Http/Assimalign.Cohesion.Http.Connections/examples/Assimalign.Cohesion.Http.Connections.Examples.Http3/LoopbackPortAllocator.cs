using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.Http.Connections.Examples.Http3;

internal static class LoopbackPortAllocator
{
    public static int AllocateUdpPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
