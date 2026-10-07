using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// End-to-end coverage for issue #1308 over real loopback sockets. A burst of clients connects and then
/// resets (SO_LINGER 0) at once, so some of them reset while they still wait in the accept queue, which
/// makes Windows fail their accept. Before the fix that failure escaped the TCP listener and the HTTP
/// accept loop stopped the endpoint. A well-behaved client must still be served afterwards, on a plain
/// endpoint and on a TLS endpoint layered over TCP.
/// </summary>
public class WebConnectionResetIsolationTests
{
    // A hang guard, never a budget.
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    private const int resetClients = 64;

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttp1: Clients that reset before the accept should not stop the endpoint")]
    public async Task UseHttp1_AfterClientsResetBeforeAccept_ShouldKeepServing()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        int port = GetAvailablePort();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, port)));

        await using WebApplication application = builder.Build();
        application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });
        await ((IHost)application).StartAsync(cancellation.Token);

        try
        {
            // Act
            await ResetClientsAsync(port, cancellation.Token);
            using HttpClient client = new();
            using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/", cancellation.Token);

            // Assert
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - UseHttps: Clients that reset before the accept should not stop the endpoint")]
    public async Task UseHttps_AfterClientsResetBeforeAccept_ShouldKeepServing()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = SelfSignedCertificateFactory.Create("localhost");
        await using TlsTestServer server = await TlsTestServer.StartAsync(
            new TlsServerOptions { AuthenticationOptions = { ServerCertificate = certificate } },
            cancellation.Token);

        // Act
        await ResetClientsAsync(server.Port, cancellation.Token);

        using SocketsHttpHandler handler = new()
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };
        using HttpClient client = new(handler);
        using HttpResponseMessage response = await client.GetAsync($"https://localhost:{server.Port}/", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // Connects every client first and only then resets them all, so the resets arrive while the server is
    // still working through the burst and some connections are still queued.
    private static async Task ResetClientsAsync(int port, CancellationToken cancellationToken)
    {
        Socket[] sockets = new Socket[resetClients];

        try
        {
            Task[] connects = new Task[resetClients];

            for (int i = 0; i < resetClients; i++)
            {
                sockets[i] = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                connects[i] = sockets[i].ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken).AsTask();
            }

            await Task.WhenAll(connects);

            foreach (Socket socket in sockets)
            {
                socket.LingerState = new LingerOption(true, 0);
            }
        }
        finally
        {
            foreach (Socket? socket in sockets)
            {
                socket?.Dispose();
            }
        }
    }

    private static int GetAvailablePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
