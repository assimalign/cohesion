using System;

namespace Assimalign.Cohesion.Net.Transports;

public static class Transport
{
    public static ITransport CreateTcpClient(Action<TcpClientTransportOptions> configure) =>
        TcpClientTransport.Create(configure);

    public static ITransport CreateTcpServer(Action<TcpServerTransportOptions> configure) =>
        TcpServerTransport.Create(configure);

    public static ITransport CreateUdpClient(Action<UdpClientTransportOptions> configure) => 
        UdpClientTransport.Create(configure);

    public static ITransport CreateUdpServer(Action<UdpServerTransportOptions> configure) => 
        UdpServerTransport.Create(configure);
}
