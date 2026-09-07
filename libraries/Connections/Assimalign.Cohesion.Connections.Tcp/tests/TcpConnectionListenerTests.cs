using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

public class TcpConnectionListenerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - BindAsync: Should bind an ephemeral endpoint explicitly")]
    public async Task BindAsync_WithEphemeralEndPoint_ShouldReflectBoundPort()
    {
        // Arrange
        await using TcpConnectionListener listener = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        // Act
        await listener.BindAsync();
        EndPoint firstBoundEndPoint = listener.EndPoint;
        await listener.BindAsync();

        // Assert
        IPEndPoint boundEndPoint = listener.EndPoint.ShouldBeOfType<IPEndPoint>();
        boundEndPoint.Port.ShouldNotBe(0);
        boundEndPoint.ShouldBe(firstBoundEndPoint);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - BindAsync: Dispose should release a fixed endpoint for a new listener")]
    public async Task BindAsync_AfterDisposedListenerOnSameEndPoint_ShouldSucceed()
    {
        // Arrange
        TcpConnectionListener first = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));

        await first.BindAsync();
        IPEndPoint endPoint = first.EndPoint.ShouldBeOfType<IPEndPoint>();

        // Act
        await first.DisposeAsync();

        await using TcpConnectionListener second = TcpConnectionListener.Create(
            options => options.EndPoint = endPoint);
        await second.BindAsync();

        // Assert
        second.EndPoint.ShouldBe(endPoint);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - BindAsync: Occupied endpoint failure should leave the listener bindable")]
    public async Task BindAsync_WithOccupiedEndPoint_ShouldThrowSocketExceptionAndRemainBindable()
    {
        // Arrange
        await using TcpConnectionListener first = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await first.BindAsync();

        TcpConnectionListener second = TcpConnectionListener.Create(
            options => options.EndPoint = first.EndPoint);

        // Act
        Exception? bindException = await Record.ExceptionAsync(async () => await second.BindAsync());
        await first.DisposeAsync();
        Exception? retryException = await Record.ExceptionAsync(async () => await second.BindAsync());
        Exception? disposeException = await Record.ExceptionAsync(async () => await second.DisposeAsync());

        // Assert
        bindException.ShouldBeOfType<SocketException>();
        retryException.ShouldBeNull();
        disposeException.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - BindAsync: Disposed listener should reject bind and accept")]
    public async Task BindAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        TcpConnectionListener listener = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await listener.DisposeAsync();

        // Act / Assert
        await Should.ThrowAsync<ObjectDisposedException>(async () => await listener.BindAsync());
        await Should.ThrowAsync<ObjectDisposedException>(async () => await listener.AcceptAsync());
    }

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
