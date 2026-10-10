using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Tcp.Tests.TestObjects;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

public class TcpConnectionListenerTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _backoffTestTimeout = TimeSpan.FromSeconds(15);

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
        using CancellationTokenSource cancellation = new(_testTimeout);

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
        using CancellationTokenSource cancellation = new(_testTimeout);
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
        using CancellationTokenSource cancellation = new(_testTimeout);

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

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - Dispose: A client connecting while an accept is cancelled is never left connected to nothing")]
    public async Task DisposeAsync_WhileClientConnectsDuringCancelledAccept_ShouldCloseTheClient()
    {
        // Arrange — the shutdown race (#1093): the accept is cancelled and the listener disposed while a
        // client connects. On Windows the OS can already have attached that client to the accept's
        // socket; unless that socket is closed, the client waits on a connection nobody owns.
        const int iterations = 100;
        int leftOpen = 0;

        for (int i = 0; i < iterations; i++)
        {
            TcpConnectionListener listener = TcpConnectionListener.Create(
                options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
            await listener.BindAsync();

            using CancellationTokenSource acceptCancellation = new();
            Task<Connection> accept = listener.AcceptAsync(acceptCancellation.Token).AsTask();

            using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Task connect = client.ConnectAsync(listener.EndPoint);

            if (i % 2 == 0)
            {
                await Task.Yield();
            }

            // Act
            acceptCancellation.Cancel();
            await listener.DisposeAsync();

            try
            {
                await connect;
            }
            catch (SocketException)
            {
                continue; // Refused after the listener closed: nothing to leak.
            }

            try
            {
                await using Connection connection = await accept;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            if (!await ClosesWithinAsync(client, TimeSpan.FromSeconds(2)))
            {
                leftOpen++;
            }
        }

        // Assert
        leftOpen.ShouldBe(0, "a client stayed connected to a socket the listener no longer owned");
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - AcceptAsync: Clients that reset while queued should not fault the listener")]
    public async Task AcceptAsync_ClientsResetWhileQueued_ShouldKeepAccepting()
    {
        // Arrange — clients that connect and reset (SO_LINGER 0) before the listener accepts them (#1308).
        // Windows fails the accept of each with ConnectionReset, and BSD-derived stacks with
        // ConnectionAborted. Linux usually returns the connection anyway, already reset. In every case
        // the listener has to go on to the healthy client queued behind them.
        using CancellationTokenSource cancellation = new(_testTimeout);

        await using TcpConnectionListener listener = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await listener.BindAsync(cancellation.Token);

        const int resetClients = 8;

        for (int i = 0; i < resetClients; i++)
        {
            using Socket reset = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await reset.ConnectAsync(listener.EndPoint, cancellation.Token);
            reset.LingerState = new LingerOption(true, 0);
        }

        using Socket healthy = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await healthy.ConnectAsync(listener.EndPoint, cancellation.Token);
        int healthyPort = ((IPEndPoint)healthy.LocalEndPoint!).Port;

        // Act
        Connection? accepted = null;

        for (int i = 0; i <= resetClients && accepted is null; i++)
        {
            Connection connection = await listener.AcceptAsync(cancellation.Token);

            if (connection.RemoteEndPoint is IPEndPoint remote && remote.Port == healthyPort)
            {
                accepted = connection;
            }
            else
            {
                await connection.DisposeAsync();
            }
        }

        // Assert
        accepted.ShouldNotBeNull();
        await accepted.DisposeAsync();
    }

    [Theory(DisplayName = "Cohesion Test [Connections.Tcp] - AcceptAsync: An accept that fails for want of descriptors or buffers should back off, retry and start over after a success")]
    [InlineData(SocketError.TooManyOpenSockets)]
    [InlineData(SocketError.NoBufferSpaceAvailable)]
    public async Task AcceptAsync_AcceptFailsForWantOfResources_ShouldBackOffRetryAndStartOverAfterSuccess(SocketError error)
    {
        // Arrange — running out of descriptors (EMFILE/ENFILE) or buffers (ENOBUFS) is transient, so the
        // listener has to wait and retry instead of failing (#1312). Eight failures take the wait through
        // 5, 10, 20, ... 640 ms, and the next failure would wait 1 s.
        using CancellationTokenSource cancellation = new(_backoffTestTimeout);
        ScriptedAccept accept = new(error, error, error, error, error, error, error, error);

        await using TcpConnectionListener listener = CreateListener(accept);
        await listener.BindAsync(cancellation.Token);

        using Socket first = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await first.ConnectAsync(listener.EndPoint, cancellation.Token);

        // Act
        await using Connection firstAccepted = await listener.AcceptAsync(cancellation.Token);
        IReadOnlyList<long> attempts = accept.AttemptTimestamps;

        accept.Fail(error);

        using Socket second = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await second.ConnectAsync(listener.EndPoint, cancellation.Token);

        Stopwatch secondAcceptTime = Stopwatch.StartNew();
        await using Connection secondAccepted = await listener.AcceptAsync(cancellation.Token);
        secondAcceptTime.Stop();

        // Assert
        attempts.Count.ShouldBe(9, "each failure is retried, and the ninth attempt accepts");
        Stopwatch.GetElapsedTime(attempts[7], attempts[8]).ShouldBeGreaterThan(
            TimeSpan.FromMilliseconds(600),
            "the wait doubles with each consecutive failure, so the eighth wait is 640 ms");
        ((IPEndPoint)firstAccepted.RemoteEndPoint!).Port.ShouldBe(((IPEndPoint)first.LocalEndPoint!).Port);

        accept.Attempts.ShouldBe(11);
        secondAcceptTime.Elapsed.ShouldBeLessThan(
            TimeSpan.FromMilliseconds(500),
            "a successful accept starts the schedule over at 5 ms instead of continuing at 1 s");
        ((IPEndPoint)secondAccepted.RemoteEndPoint!).Port.ShouldBe(((IPEndPoint)second.LocalEndPoint!).Port);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - AcceptAsync: Cancelling during a back-off should end the accept at once")]
    public async Task AcceptAsync_CanceledDuringBackoff_ShouldThrowOperationCanceledExceptionAtOnce()
    {
        // Arrange — the ninth consecutive failure starts a 1 s wait.
        using CancellationTokenSource timeout = new(_backoffTestTimeout);
        using CancellationTokenSource acceptCancellation = new();
        ScriptedAccept accept = ScriptedAccept.FailingForever(SocketError.TooManyOpenSockets);

        await using TcpConnectionListener listener = CreateListener(accept);
        await listener.BindAsync(timeout.Token);

        Task<Connection> acceptTask = listener.AcceptAsync(acceptCancellation.Token).AsTask();
        await accept.WaitForAttemptsAsync(9, acceptTask, timeout.Token);
        acceptTask.IsCompleted.ShouldBeFalse("the listener should be backing off, not failing");

        // Act
        Stopwatch elapsed = Stopwatch.StartNew();
        acceptCancellation.Cancel();
        Exception? exception = await Record.ExceptionAsync(() => acceptTask);
        elapsed.Stop();

        // Assert
        exception.ShouldBeAssignableTo<OperationCanceledException>();
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500), "cancellation ends the 1 s wait");
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - AcceptAsync: Disposing the listener during a back-off should end the accept at once")]
    public async Task AcceptAsync_DisposedDuringBackoff_ShouldThrowObjectDisposedExceptionAtOnce()
    {
        // Arrange — the ninth consecutive failure starts a 1 s wait.
        using CancellationTokenSource timeout = new(_backoffTestTimeout);
        ScriptedAccept accept = ScriptedAccept.FailingForever(SocketError.NoBufferSpaceAvailable);

        await using TcpConnectionListener listener = CreateListener(accept);
        await listener.BindAsync(timeout.Token);

        Task<Connection> acceptTask = listener.AcceptAsync(timeout.Token).AsTask();
        await accept.WaitForAttemptsAsync(9, acceptTask, timeout.Token);
        acceptTask.IsCompleted.ShouldBeFalse("the listener should be backing off, not failing");

        // Act
        Stopwatch elapsed = Stopwatch.StartNew();
        await listener.DisposeAsync();
        Exception? exception = await Record.ExceptionAsync(() => acceptTask);
        elapsed.Stop();

        // Assert
        exception.ShouldBeOfType<ObjectDisposedException>();
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(500), "disposal ends the 1 s wait");
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - AcceptAsync: A network error pending on a queued connection should be skipped on Linux and escape elsewhere")]
    public async Task AcceptAsync_PendingNetworkError_ShouldBeSkippedOnLinuxOnly()
    {
        // Arrange — Linux accept(2) reports a network error pending on the new socket as the accept's error,
        // and the same SocketError means the listening socket failed on Windows.
        using CancellationTokenSource cancellation = new(_testTimeout);
        ScriptedAccept accept = new(SocketError.NetworkUnreachable);

        await using TcpConnectionListener listener = CreateListener(accept);
        await listener.BindAsync(cancellation.Token);

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint, cancellation.Token);

        // Act
        Exception? exception = await Record.ExceptionAsync(async () =>
        {
            await using Connection connection = await listener.AcceptAsync(cancellation.Token);
        });

        // Assert
        if (OperatingSystem.IsLinux())
        {
            exception.ShouldBeNull();
            accept.Attempts.ShouldBe(2);
        }
        else
        {
            exception.ShouldBeOfType<SocketException>().SocketErrorCode.ShouldBe(SocketError.NetworkUnreachable);
            accept.Attempts.ShouldBe(1);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - AcceptAsync: An error of the listening socket itself should escape without a retry")]
    public async Task AcceptAsync_ListeningSocketError_ShouldEscapeWithoutRetry()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        ScriptedAccept accept = new(SocketError.InvalidArgument);

        await using TcpConnectionListener listener = CreateListener(accept);

        // Act
        Exception? exception = await Record.ExceptionAsync(async () => await listener.AcceptAsync(cancellation.Token));

        // Assert
        exception.ShouldBeOfType<SocketException>().SocketErrorCode.ShouldBe(SocketError.InvalidArgument);
        accept.Attempts.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - AcceptAsync: An accepted socket that cannot be set up should be closed and the next connection accepted")]
    public async Task AcceptAsync_AcceptedSocketSetupFails_ShouldCloseSocketAndAcceptNext()
    {
        // Arrange — setting up an accepted socket can fail after the accept succeeded: on macOS, setting
        // TCP_NODELAY fails with EINVAL once the client has reset the connection. A bound UDP socket stands in
        // for it, because setting TCP_NODELAY on one fails on every platform (InvalidArgument on Windows,
        // ProtocolOption on Linux). That failure is the one connection's, so the listener has to close the
        // socket and go on to the healthy client, not stop or leave the socket open.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using Socket unusable = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        unusable.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        ScriptedAccept accept = new();
        accept.Return(unusable);

        await using TcpConnectionListener listener = CreateListener(accept);
        await listener.BindAsync(cancellation.Token);

        using Socket healthy = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await healthy.ConnectAsync(listener.EndPoint, cancellation.Token);

        // Act
        await using Connection accepted = await listener.AcceptAsync(cancellation.Token);

        // Assert
        accept.Attempts.ShouldBe(2, "the connection that failed set-up is skipped and the next one accepted");
        ((IPEndPoint)accepted.RemoteEndPoint!).Port.ShouldBe(((IPEndPoint)healthy.LocalEndPoint!).Port);
        Should.Throw<ObjectDisposedException>(() => unusable.Available, "the listener closes a socket it could not set up");
    }

    private static TcpConnectionListener CreateListener(ScriptedAccept accept)
        => new(new TcpConnectionListenerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) }, accept.AcceptAsync);

    private static async Task<bool> ClosesWithinAsync(Socket client, TimeSpan budget)
    {
        using CancellationTokenSource timeout = new(budget);

        try
        {
            await client.ReceiveAsync(new byte[16], SocketFlags.None, timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }
}
