using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

public class TcpConnectionFactoryTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConnectAsync_ToNonListeningEndPoint_ShouldThrowSocketException()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        IPEndPoint deadEndPoint = GetClosedLoopbackEndPoint();

        TcpConnectionFactory factory = new();

        // Act / Assert
        await Should.ThrowAsync<SocketException>(async () => await factory.ConnectAsync(deadEndPoint, cancellation.Token));
    }

    [Fact]
    public async Task ConnectAsync_TypedAndInterface_ShouldReturnTcpConnection()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        using Socket listenerSocket = CreateLoopbackListenerSocket(out IPEndPoint endPoint);

        TcpConnectionFactory factory = new();

        // Act
        await using Connection typed = await factory.ConnectAsync(endPoint, cancellation.Token);
        await using IConnection viaInterface = await ((IConnectionFactory)factory).ConnectAsync(endPoint, cancellation.Token);

        // Assert
        // The guided base forwards the explicit interface implementation to the typed overload,
        // so both calls produce the same concrete driver connection type.
        typed.ShouldBeOfType<TcpConnection>();
        viaInterface.ShouldBeOfType<TcpConnection>();
    }

    [Fact]
    public void Capabilities_OnFactory_ShouldDescribeReliableOrderedTcpStream()
    {
        // Arrange
        TcpConnectionFactory factory = new();

        // Act
        ConnectionCapabilities capabilities = factory.Capabilities;

        // Assert
        capabilities.ShouldBe(new ConnectionCapabilities(
            ConnectionProtocol.Tcp,
            ConnectionDelivery.Stream,
            IsReliable: true,
            IsOrdered: true,
            IsMultiplexed: false,
            ConnectionSecurity.None));
    }

    [Fact]
    public async Task ConnectAsync_WithNullEndPoint_ShouldThrowArgumentNullException()
    {
        // Arrange
        TcpConnectionFactory factory = new();

        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(async () => await factory.ConnectAsync(null!));
    }

    [Fact]
    public void Constructor_WithNullOptions_ShouldThrowArgumentNullException()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentNullException>(() => new TcpConnectionFactory(null!));
    }

    [Fact]
    public void Create_WithNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentNullException>(() => TcpConnectionFactory.Create(null!));
    }

    private static IPEndPoint GetClosedLoopbackEndPoint()
    {
        // Bind an ephemeral port, capture it, and close the listener so the port is known to be
        // unoccupied when the factory attempts to connect.
        TcpListener listener = new(IPAddress.Loopback, 0);

        listener.Start();

        IPEndPoint endPoint = (IPEndPoint)listener.LocalEndpoint;

        listener.Stop();

        return endPoint;
    }

    private static Socket CreateLoopbackListenerSocket(out IPEndPoint endPoint)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        socket.Listen(8);

        endPoint = (IPEndPoint)socket.LocalEndPoint!;

        return socket;
    }
}
