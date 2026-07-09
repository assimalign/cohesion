using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

/// <summary>
/// Covers the Unix domain socket path of the stream driver: the socket-file lifecycle
/// (delete-stale-before-bind, unlink-on-dispose) and honest <see cref="ConnectionProtocol"/> stamping.
/// Mirrors the datagram-UDS coverage in the Udp driver's tests.
/// </summary>
public class UnixDomainSocketTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - Uds: Should round-trip bytes over a Unix domain socket")]
    public async Task AcceptAsync_OverUnixDomainSocket_ShouldRoundTripBytes()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        string path = UnixSocketPath.Create();

        try
        {
            await using TcpConnectionListener listener = TcpConnectionListener.Create(
                options => options.EndPoint = new UnixDomainSocketEndPoint(path));

            ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

            TcpConnectionFactory factory = new();

            await using Connection client = await factory.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellation.Token);
            await using Connection server = await acceptTask;

            // Act / Assert — client -> server
            await client.Output.WriteAsync(Encoding.UTF8.GetBytes("ping"), cancellation.Token);
            await client.Output.FlushAsync(cancellation.Token);

            byte[] received = await server.Input.ReadExactlyAsync(4, cancellation.Token);
            Encoding.UTF8.GetString(received).ShouldBe("ping");

            // Act / Assert — server -> client
            await server.Output.WriteAsync(Encoding.UTF8.GetBytes("pong!"), cancellation.Token);
            await server.Output.FlushAsync(cancellation.Token);

            byte[] echoed = await client.Input.ReadExactlyAsync(5, cancellation.Token);
            Encoding.UTF8.GetString(echoed).ShouldBe("pong!");
        }
        finally
        {
            SafeDelete(path);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - Uds: Should report the UnixDomainSocket protocol on the listener")]
    public async Task Capabilities_OnUnixDomainSocketListener_ShouldReportUnixDomainSocket()
    {
        // Arrange
        string path = UnixSocketPath.Create();

        await using TcpConnectionListener listener = TcpConnectionListener.Create(
            options => options.EndPoint = new UnixDomainSocketEndPoint(path));

        // Act
        ConnectionCapabilities capabilities = listener.Capabilities;

        // Assert — same delivery guarantees as TCP, but honestly stamped as a Unix domain socket.
        capabilities.ShouldBe(new ConnectionCapabilities(
            ConnectionProtocol.UnixDomainSocket,
            ConnectionDelivery.Stream,
            IsReliable: true,
            IsOrdered: true,
            IsMultiplexed: false,
            ConnectionSecurity.None));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - Uds: Should stamp accepted and dialed connections as UnixDomainSocket")]
    public async Task Connections_OverUnixDomainSocket_ShouldStampUnixDomainSocketProtocol()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        string path = UnixSocketPath.Create();

        try
        {
            await using TcpConnectionListener listener = TcpConnectionListener.Create(
                options => options.EndPoint = new UnixDomainSocketEndPoint(path));

            ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

            TcpConnectionFactory factory = new();

            // Act
            await using Connection client = await factory.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellation.Token);
            await using Connection server = await acceptTask;

            // Assert — both the dialed and the accepted connection stamp the honest protocol.
            client.Capabilities.Protocol.ShouldBe(ConnectionProtocol.UnixDomainSocket);
            server.Capabilities.Protocol.ShouldBe(ConnectionProtocol.UnixDomainSocket);
        }
        finally
        {
            SafeDelete(path);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - Uds: Should rebind after a stale socket file is left by an unclean shutdown")]
    public async Task AcceptAsync_WhenStaleSocketFileExists_ShouldDeleteItAndBind()
    {
        // Arrange — a stale filesystem entry left at the path (as an unclean shutdown would leave a
        // socket file) occupies the address; binding to it without cleanup fails with AddressAlreadyInUse.
        // A plain file stands in for the stale socket file so the test is deterministic across platforms
        // (Windows removes AF_UNIX socket files on close; Linux and macOS do not).
        using CancellationTokenSource cancellation = new(TestTimeout);
        string path = UnixSocketPath.Create();

        try
        {
            File.WriteAllBytes(path, Array.Empty<byte>());

            File.Exists(path).ShouldBeTrue("the stale file should occupy the socket path before binding");

            // Act — a fresh listener must delete the stale file before binding, rather than failing with
            // AddressAlreadyInUse.
            await using TcpConnectionListener listener = TcpConnectionListener.Create(
                options => options.EndPoint = new UnixDomainSocketEndPoint(path));

            ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

            TcpConnectionFactory factory = new();

            await using Connection client = await factory.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellation.Token);
            await using Connection server = await acceptTask;

            // Assert — the rebind succeeded and the connection is live.
            server.ShouldNotBeNull();
            server.State.ShouldBe(ConnectionState.Open);
        }
        finally
        {
            SafeDelete(path);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - Uds: Should unlink the socket file on dispose")]
    public async Task DisposeAsync_OverUnixDomainSocket_ShouldUnlinkSocketFile()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        string path = UnixSocketPath.Create();

        try
        {
            TcpConnectionListener listener = TcpConnectionListener.Create(
                options => options.EndPoint = new UnixDomainSocketEndPoint(path));

            ValueTask<Connection> acceptTask = listener.AcceptAsync(cancellation.Token);

            TcpConnectionFactory factory = new();

            await using (Connection client = await factory.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellation.Token))
            await using (Connection server = await acceptTask)
            {
                // The bind created the socket file.
                File.Exists(path).ShouldBeTrue("binding a Unix domain socket creates its socket file");
            }

            // Act
            await listener.DisposeAsync();

            // Assert — disposal unlinks the socket file.
            File.Exists(path).ShouldBeFalse("disposing the listener should unlink the socket file");
        }
        finally
        {
            SafeDelete(path);
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
