using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

public class TcpConnectionListenerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Create_WithNullConfigure_ShouldThrowArgumentNullException()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentNullException>(() => TcpConnectionListener.Create(null!));
    }

    [Fact]
    public void Constructor_WithNullOptions_ShouldThrowArgumentNullException()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentNullException>(() => new TcpConnectionListener(null!));
    }

    [Fact]
    public async Task AcceptAsync_WithEphemeralEndPoint_ShouldBindAndReflectBoundPort()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        await using TcpConnectionListener listener = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        // Before the first accept the listener is unbound and reports the configured endpoint.
        ((IPEndPoint)listener.EndPoint).Port.ShouldBe(0);

        // Act
        // The listener binds lazily on the first accept; the bind happens in the synchronous
        // prefix of AcceptAsync, so the ephemeral endpoint is available once the call returns
        // its pending task.
        ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

        IPEndPoint boundEndPoint = (IPEndPoint)listener.EndPoint;

        TcpConnectionFactory factory = new();

        await using Connection client = await factory.ConnectAsync(boundEndPoint, cancellation.Token);
        await using Connection server = await acceptTask;

        // Assert
        boundEndPoint.Port.ShouldNotBe(0);
        boundEndPoint.Address.ShouldBe(IPAddress.Loopback);
        ((IPEndPoint)server.LocalEndPoint!).Port.ShouldBe(boundEndPoint.Port);
    }

    [Fact]
    public async Task Capabilities_OnListener_ShouldDescribeReliableOrderedTcpStream()
    {
        // Arrange
        await using TcpConnectionListener listener = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        // Act
        ConnectionCapabilities capabilities = listener.Capabilities;

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
    public async Task AcceptAsync_WhenCanceled_ShouldThrowOperationCanceledException()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        using CancellationTokenSource acceptCancellation = new();

        await using TcpConnectionListener listener = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        ValueTask<Connection> acceptTask = listener.AcceptAsync(acceptCancellation.Token);

        // Act
        acceptCancellation.Cancel();

        Exception? exception = await Record.ExceptionAsync(async () => await acceptTask);

        // Assert
        exception.ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_ShouldBeIdempotent()
    {
        // Arrange
        TcpConnectionListener listener = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        // Act
        await listener.DisposeAsync();

        Exception? exception = await Record.ExceptionAsync(async () => await listener.DisposeAsync());

        // Assert
        exception.ShouldBeNull();
    }

    [Fact]
    public async Task DisposeAsync_WithLiveAcceptedConnection_ShouldCloseTrackedConnection()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);

        TcpConnectionListener listener = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

        TcpConnectionFactory factory = new();

        await using Connection client = await factory.ConnectAsync(listener.EndPoint, cancellation.Token);
        await using Connection server = await acceptTask;

        // Act
        await listener.DisposeAsync();

        // Assert
        // The listener disposes every tracked live connection, and a connection's dispose only
        // returns after its pump loops have finished and signaled closure.
        server.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();
        server.State.ShouldBe(ConnectionState.Closed);
    }
}
