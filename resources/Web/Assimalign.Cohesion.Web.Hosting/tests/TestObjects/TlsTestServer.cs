using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Security;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// A Web application serving one <c>UseHttps</c> endpoint on a free loopback port, whose terminal
/// middleware records the TLS session of the first request it serves and answers <c>200</c>.
/// </summary>
internal sealed class TlsTestServer : IAsyncDisposable
{
    private readonly IWebApplicationServer _server;

    private TlsTestServer(IWebApplicationServer server, int port, TaskCompletionSource<ObservedSession> observed)
    {
        _server = server;
        Port = port;
        Observed = observed;
    }

    /// <summary>Gets the loopback port the endpoint listens on.</summary>
    public int Port { get; }

    /// <summary>Gets the TLS session the first served request observed.</summary>
    public TaskCompletionSource<ObservedSession> Observed { get; }

    /// <summary>
    /// Builds the application, registers the endpoint with <paramref name="tlsOptions"/>, and starts
    /// the server; the endpoint is bound when this returns.
    /// </summary>
    public static async Task<TlsTestServer> StartAsync(TlsServerOptions tlsOptions, CancellationToken cancellationToken)
    {
        int port = GetAvailablePort();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttps(tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, port), tlsOptions));
        WebApplication app = builder.Build();

        TaskCompletionSource<ObservedSession> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Use((context, next) =>
        {
            observed.TrySetResult(ObservedSession.From(context.TlsConnection));
            context.Response.StatusCode = Assimalign.Cohesion.Http.HttpStatusCode.Ok;
            return Task.CompletedTask;
        });

        IWebApplicationServer server = app.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();
        await server.StartAsync(cancellationToken).ConfigureAwait(false);

        return new TlsTestServer(server, port, observed);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static int GetAvailablePort()
    {
        using TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
