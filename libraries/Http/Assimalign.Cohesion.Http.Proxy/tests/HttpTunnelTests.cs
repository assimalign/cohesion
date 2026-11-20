using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Tests;

public sealed class HttpTunnelTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - RelayAsync: Should connect, accept and relay until both sides finish")]
    public async Task RelayAsync_Connect_ShouldRelayAndHalfClose()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Task serving = ServeEchoAsync(listener, timeout.Token);
        (NetworkStream server, NetworkStream client) = await Loopback.CreateSocketPairAsync();
        await using NetworkStream clientEnd = client;
        TestHttpContext context = CreateConnectContext(server, out TestProtocolUpgrade upgrade);
        HttpTunnel tunnel = HttpTunnel.Create();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Act
        Task<HttpProxyResult> relaying = tunnel.RelayAsync(context, new DnsEndPoint("127.0.0.1", port), timeout.Token);
        await clientEnd.WriteAsync(Encoding.ASCII.GetBytes("hello"), timeout.Token);
        clientEnd.Socket.Shutdown(SocketShutdown.Send);
        string echoed = await ReadToEndAsync(clientEnd, timeout.Token);
        HttpProxyResult result = await relaying;
        await serving;

        // Assert
        result.ShouldBe(HttpProxyResult.Completed);
        echoed.ShouldBe("hello");
        upgrade.Accepted.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - RelayAsync: Should connect through the callback and end when the destination closes")]
    public async Task RelayAsync_ConnectCallback_ShouldRelayToReturnedStream()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        (NetworkStream destinationServer, NetworkStream destinationClient) = await Loopback.CreateSocketPairAsync();
        await using NetworkStream destinationEnd = destinationServer;
        (NetworkStream server, NetworkStream client) = await Loopback.CreateSocketPairAsync();
        await using NetworkStream clientEnd = client;
        TestHttpContext context = CreateConnectContext(server, out TestProtocolUpgrade upgrade);
        DnsEndPoint? requested = null;
        HttpTunnel tunnel = HttpTunnel.Create(new HttpTunnelOptions
        {
            ConnectCallback = (endPoint, _) =>
            {
                requested = endPoint;
                return ValueTask.FromResult<Stream>(destinationClient);
            },
        });

        // Act
        Task<HttpProxyResult> relaying = tunnel.RelayAsync(context, new DnsEndPoint("backend.test", 443), timeout.Token);
        await clientEnd.WriteAsync(Encoding.ASCII.GetBytes("hello"), timeout.Token);
        string atDestination = await Loopback.ReadTextAsync(destinationEnd, 5, timeout.Token);
        await destinationEnd.WriteAsync(Encoding.ASCII.GetBytes("world"), timeout.Token);
        destinationEnd.Socket.Shutdown(SocketShutdown.Send);
        string atClient = await ReadToEndAsync(clientEnd, timeout.Token);
        HttpProxyResult result = await relaying;

        // Assert
        result.ShouldBe(HttpProxyResult.Completed);
        atDestination.ShouldBe("hello");
        atClient.ShouldBe("world");
        upgrade.Accepted.ShouldBeTrue();
        requested.ShouldNotBeNull().Host.ShouldBe("backend.test");
        requested.Port.ShouldBe(443);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - RelayAsync: Should answer a refused connection with 502 before accepting")]
    public async Task RelayAsync_DestinationRefused_ShouldAnswerBadGateway()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        TestHttpContext context = CreateConnectContext(Stream.Null, out TestProtocolUpgrade upgrade);
        HttpTunnel tunnel = HttpTunnel.Create();

        // Act
        HttpProxyResult result = await tunnel.RelayAsync(context, new DnsEndPoint("127.0.0.1", Loopback.GetUnusedPort()), timeout.Token);

        // Assert
        result.ShouldBe(HttpProxyResult.DestinationUnreachable);
        ((int)context.Response.StatusCode).ShouldBe(502);
        upgrade.Accepted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - RelayAsync: Should answer a connection that does not complete in time with 504")]
    public async Task RelayAsync_ConnectTimesOut_ShouldAnswerGatewayTimeout()
    {
        // Arrange
        TestHttpContext context = CreateConnectContext(Stream.Null, out TestProtocolUpgrade upgrade);
        HttpTunnel tunnel = HttpTunnel.Create(new HttpTunnelOptions
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
            ConnectCallback = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return Stream.Null;
            },
        });

        // Act
        HttpProxyResult result = await tunnel.RelayAsync(context, new DnsEndPoint("backend.test", 443), CancellationToken.None);

        // Assert
        result.ShouldBe(HttpProxyResult.DestinationTimedOut);
        ((int)context.Response.StatusCode).ShouldBe(504);
        upgrade.Accepted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - RelayAsync: Should abort the exchange when the caller gives up while connecting")]
    public async Task RelayAsync_CallerCanceled_ShouldAbortExchange()
    {
        // Arrange
        using CancellationTokenSource cancellation = new();
        TestHttpContext context = CreateConnectContext(Stream.Null, out TestProtocolUpgrade upgrade);
        HttpTunnel tunnel = HttpTunnel.Create(new HttpTunnelOptions
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                await cancellation.CancelAsync();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return Stream.Null;
            },
        });

        // Act
        HttpProxyResult result = await tunnel.RelayAsync(context, new DnsEndPoint("backend.test", 443), cancellation.Token);

        // Assert
        result.ShouldBe(HttpProxyResult.Canceled);
        context.IsCanceled.ShouldBeTrue();
        upgrade.Accepted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - RelayAsync: Should answer a request that is not an HTTP/1.1 CONNECT with 501")]
    public async Task RelayAsync_NotConnect_ShouldAnswerNotImplemented()
    {
        // Arrange
        HttpTunnel tunnel = HttpTunnel.Create(new HttpTunnelOptions
        {
            ConnectCallback = (_, _) => throw new InvalidOperationException("The tunnel must not connect."),
        });
        TestHttpContext plain = new();
        TestHttpContext upgradeRequest = new();
        upgradeRequest.Features.Set(new TestProtocolUpgrade(upgradeRequest, HttpProtocolUpgradeKind.Upgrade, "websocket", Stream.Null));

        // Act
        HttpProxyResult plainResult = await tunnel.RelayAsync(plain, new DnsEndPoint("backend.test", 443), CancellationToken.None);
        HttpProxyResult upgradeResult = await tunnel.RelayAsync(upgradeRequest, new DnsEndPoint("backend.test", 443), CancellationToken.None);

        // Assert
        plainResult.ShouldBe(HttpProxyResult.NotSupported);
        ((int)plain.Response.StatusCode).ShouldBe(501);
        upgradeResult.ShouldBe(HttpProxyResult.NotSupported);
        ((int)upgradeRequest.Response.StatusCode).ShouldBe(501);
    }

    private static TestHttpContext CreateConnectContext(Stream clientStream, out TestProtocolUpgrade upgrade)
    {
        TestHttpContext context = new();
        context.Request.Method = HttpMethod.Connect;
        upgrade = new TestProtocolUpgrade(context, HttpProtocolUpgradeKind.Connect, null, clientStream);
        context.Features.Set(upgrade);
        return context;
    }

    private static async Task ServeEchoAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using Socket socket = await listener.AcceptSocketAsync(cancellationToken);
        await using NetworkStream stream = new(socket, ownsSocket: false);
        await Loopback.EchoAsync(stream, cancellationToken);
    }

    private static async Task<string> ReadToEndAsync(Stream stream, CancellationToken cancellationToken)
    {
        using MemoryStream received = new();
        await stream.CopyToAsync(received, cancellationToken);
        return Encoding.ASCII.GetString(received.ToArray());
    }
}
