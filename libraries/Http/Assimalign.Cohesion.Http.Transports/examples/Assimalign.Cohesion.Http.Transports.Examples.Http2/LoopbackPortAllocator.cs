using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.Http.Transports.Examples.Http2;

internal static class LoopbackPortAllocator
{
    public static int AllocateTcpPort()
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
