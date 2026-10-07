using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// A real Web application on a loopback TCP port: the default server (with its default upgrade
/// interceptor and drain signal), <c>UseWebSockets</c>, and one terminal handler.
/// </summary>
internal sealed class WebSocketTestServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly TcpConnectionListener _listener;

    private WebSocketTestServer(WebApplication application, TcpConnectionListener listener)
    {
        _application = application;
        _listener = listener;
    }

    /// <summary>Gets the loopback port the server listens on.</summary>
    public int Port => ((IPEndPoint)_listener.EndPoint).Port;

    /// <summary>Gets the default server, to stop it with an explicit drain budget.</summary>
    public IWebApplicationServer Server => _application.Context.Servers.First();

    /// <summary>Gets the <c>ws://</c> address of <paramref name="path"/> on the server.</summary>
    public Uri WebSocketUri(string path = "/ws") => new($"ws://127.0.0.1:{Port}{path}");

    /// <summary>Gets the server's own origin, as a same-site page would send it.</summary>
    public string Origin => $"http://127.0.0.1:{Port}";

    public static async Task<WebSocketTestServer> StartAsync(
        Action<WebSocketOptions>? configure,
        Func<IHttpContext, Task> handler,
        CancellationToken cancellationToken)
    {
        TcpConnectionListener listener = new(new TcpConnectionListenerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        });

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(listener));

        WebApplication application = builder.Build();
        application.UseWebSockets(configure);
        application.Use((context, next) => handler(context));

        await ((IWebApplication)application).StartAsync(cancellationToken);

        return new WebSocketTestServer(application, listener);
    }

    public async ValueTask DisposeAsync()
    {
        await ((IWebApplication)_application).StopAsync(CancellationToken.None);
        await ((IAsyncDisposable)_application).DisposeAsync();
    }
}
