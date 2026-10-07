using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// The protocol-upgrade interceptor the default server installs (decision 16, Http ADR 1), over a
/// real loopback HTTP/1.1 connection: an upgrade reaches <c>context.Upgrade</c> with no listener
/// configuration, an accepted one switches protocols, and one nobody accepts is served as before.
/// </summary>
public class WebApplicationProtocolUpgradeTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Upgrade: An upgrade no handler accepts is served exactly as before")]
    public async Task DefaultServer_UnacceptedUpgrade_IsServedAsAnOrdinaryRequest()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        string? protocol = null;

        await using UpgradeTestServer server = await UpgradeTestServer.StartAsync(async context =>
        {
            protocol = context.Upgrade?.Protocol;
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.ASCII.GetBytes("served"), context.RequestCancelled);
        }, cancellationToken);

        using Socket socket = await ConnectAsync(server.Port, cancellationToken);
        await using NetworkStream stream = new(socket, ownsSocket: false);

        // Act
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /chat HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: Upgrade\r\nUpgrade: example/1\r\n\r\n"), cancellationToken);
        string response = await ReadUntilAsync(stream, "served", cancellationToken);

        // Assert
        protocol.ShouldBe("example/1");
        response.ShouldStartWith("HTTP/1.1 200");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Upgrade: An accepted upgrade switches protocols and hands the handler the raw connection")]
    public async Task DefaultServer_AcceptedUpgrade_SwitchesProtocolsAndHandsOverTheConnection()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;

        await using UpgradeTestServer server = await UpgradeTestServer.StartAsync(async context =>
        {
            IHttpProtocolUpgrade upgrade = context.Upgrade ?? throw new InvalidOperationException("No upgrade was surfaced.");
            await using Stream tunnel = await upgrade.AcceptAsync(context.RequestCancelled);

            // Echo one line in the switched-to protocol.
            byte[] buffer = new byte[64];
            int read = await tunnel.ReadAsync(buffer, context.RequestCancelled);
            await tunnel.WriteAsync(buffer.AsMemory(0, read), context.RequestCancelled);
            await tunnel.FlushAsync(context.RequestCancelled);
        }, cancellationToken);

        using Socket socket = await ConnectAsync(server.Port, cancellationToken);
        await using NetworkStream stream = new(socket, ownsSocket: false);

        // Act
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "GET /chat HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: Upgrade\r\nUpgrade: example/1\r\n\r\n"), cancellationToken);
        string head = await ReadUntilAsync(stream, "\r\n\r\n", cancellationToken);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("raw bytes"), cancellationToken);
        string echoed = await ReadUntilAsync(stream, "raw bytes", cancellationToken);

        // Assert
        head.ShouldStartWith("HTTP/1.1 101 Switching Protocols");
        head.ShouldContain("Upgrade: example/1");
        echoed.ShouldBe("raw bytes");
    }

    private static async Task<Socket> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
        return socket;
    }

    private static async Task<string> ReadUntilAsync(Stream stream, string terminator, CancellationToken cancellationToken)
    {
        StringBuilder received = new();
        byte[] one = new byte[1];

        while (!received.ToString().EndsWith(terminator, StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, cancellationToken) == 0)
            {
                break;
            }

            received.Append((char)one[0]);
        }

        return received.ToString();
    }

    private sealed class UpgradeTestServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly TcpConnectionListener _listener;

        private UpgradeTestServer(WebApplication application, TcpConnectionListener listener)
        {
            _application = application;
            _listener = listener;
        }

        public int Port => ((IPEndPoint)_listener.EndPoint).Port;

        public static async Task<UpgradeTestServer> StartAsync(Func<IHttpContext, Task> handler, CancellationToken cancellationToken)
        {
            TcpConnectionListener listener = new(new TcpConnectionListenerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
            WebApplicationBuilder builder = WebApplication.CreateBuilder();

            // No interceptor is registered here: the upgrade interceptor is the server's default.
            builder.Server.UseServer(options => options.UseHttp1(listener));

            WebApplication application = builder.Build();
            application.Use((context, next) => handler(context));
            await ((IWebApplication)application).StartAsync(cancellationToken);

            return new UpgradeTestServer(application, listener);
        }

        public async ValueTask DisposeAsync()
        {
            await ((IWebApplication)_application).StopAsync(CancellationToken.None);
            await ((IAsyncDisposable)_application).DisposeAsync();
        }
    }
}
