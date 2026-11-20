using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;

namespace Assimalign.Cohesion.Web.Proxy.Tests.TestObjects;

/// <summary>
/// A real Web application on a loopback TCP port, serving HTTP/1.1 with the default server and its
/// default interceptors (so <c>CONNECT</c> and the WebSocket upgrade work as they do in production).
/// The tests use it as a proxy's destination and as a forward proxy that clients dial.
/// </summary>
internal sealed class ProxyTestServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly TcpConnectionListener _listener;

    private ProxyTestServer(WebApplication application, TcpConnectionListener listener)
    {
        _application = application;
        _listener = listener;
    }

    /// <summary>Gets the loopback port the server listens on.</summary>
    public int Port => ((IPEndPoint)_listener.EndPoint).Port;

    /// <summary>Gets the server's <c>http://127.0.0.1:port/</c> address.</summary>
    public Uri Uri => new($"http://127.0.0.1:{Port}/");

    /// <summary>Gets the server's <c>host:port</c> authority.</summary>
    public string Authority => $"127.0.0.1:{Port}";

    /// <summary>Gets the application, for its settings.</summary>
    public WebApplication Application => _application;

    /// <summary>
    /// Starts a server whose only middleware is <paramref name="handler"/>.
    /// </summary>
    public static Task<ProxyTestServer> StartAsync(Func<IHttpContext, Task> handler, CancellationToken cancellationToken)
    {
        return StartAsync(
            static _ => { },
            application => application.Use((context, next) => handler(context)),
            cancellationToken);
    }

    /// <summary>
    /// Starts a server whose builder and pipeline the test composes.
    /// </summary>
    public static async Task<ProxyTestServer> StartAsync(
        Action<WebApplicationBuilder> build,
        Action<WebApplication> compose,
        CancellationToken cancellationToken)
    {
        TcpConnectionListener listener = new(new TcpConnectionListenerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
        });

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(listener));
        build(builder);

        WebApplication application = builder.Build();
        compose(application);

        await ((IWebApplication)application).StartAsync(cancellationToken);

        return new ProxyTestServer(application, listener);
    }

    public async ValueTask DisposeAsync()
    {
        await ((IWebApplication)_application).StopAsync(CancellationToken.None);
        await ((IAsyncDisposable)_application).DisposeAsync();
    }
}
