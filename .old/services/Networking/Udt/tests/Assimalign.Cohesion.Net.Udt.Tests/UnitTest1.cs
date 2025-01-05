using System.Net;
using System.Net.Sockets;

namespace Assimalign.PanopticNet.Udt.Tests
{
    using Internal;

    public class UnitTest1
    {
        [Fact]
        public void Test1()
        {
            var endpoint = new IPEndPoint(IPAddress.Loopback, 8080);
            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Udp);

            socket.Bind(endpoint);

            var udt = new UdtSocket(endpoint.AddressFamily, SocketType.Stream);

            udt.Bind(socket);

            socket.Listen();
            udt.Listen(10);

            var udt1 = udt.Accept();
        }
    }
}