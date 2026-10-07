using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

/// <summary>
/// The protocol a <see cref="WebSocketTestServer"/> serves.
/// </summary>
internal enum WebSocketTestProtocol
{
    /// <summary>HTTP/1.1 over loopback TCP: the RFC 6455 upgrade.</summary>
    Http1,

    /// <summary>Prior-knowledge cleartext HTTP/2 over loopback TCP: the RFC 8441 extended CONNECT.</summary>
    Http2,

    /// <summary>HTTP/3 over the in-memory multiplexed driver: the RFC 9220 extended CONNECT.</summary>
    Http3,
}

/// <summary>
/// A real Web application: the default server (with its default upgrade interceptor and drain
/// signal), <c>UseWebSockets</c>, and one terminal handler. HTTP/1.1 and HTTP/2 listen on a loopback
/// TCP port; HTTP/3 listens on an in-memory multiplexed listener (<see cref="Http3Listener"/>), which
/// needs neither QUIC nor a certificate.
/// </summary>
internal sealed class WebSocketTestServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly TcpConnectionListener? _tcpListener;

    private WebSocketTestServer(
        WebApplication application,
        TcpConnectionListener? tcpListener,
        InMemoryMultiplexedConnectionListener? http3Listener)
    {
        _application = application;
        _tcpListener = tcpListener;
        Http3Listener = http3Listener;
    }

    /// <summary>Gets the loopback port the server listens on (HTTP/1.1 and HTTP/2 only).</summary>
    public int Port => ((IPEndPoint)(_tcpListener ?? throw new InvalidOperationException("The HTTP/3 server has no TCP port.")).EndPoint).Port;

    /// <summary>Gets the in-memory listener an HTTP/3 server listens on; <see langword="null"/> otherwise.</summary>
    public InMemoryMultiplexedConnectionListener? Http3Listener { get; }

    /// <summary>Gets the default server, to stop it with an explicit drain budget.</summary>
    public IWebApplicationServer Server => _application.Context.Servers.First();

    /// <summary>Gets the <c>ws://</c> address of <paramref name="path"/> on the server.</summary>
    public Uri WebSocketUri(string path = "/ws") => new($"ws://127.0.0.1:{Port}{path}");

    /// <summary>Gets the server's own origin, as a same-site page would send it.</summary>
    public string Origin => $"http://127.0.0.1:{Port}";

    public static Task<WebSocketTestServer> StartAsync(
        Action<WebSocketOptions>? configure,
        Func<IHttpContext, Task> handler,
        CancellationToken cancellationToken)
    {
        return StartAsync(WebSocketTestProtocol.Http1, configure, handler, cancellationToken);
    }

    public static async Task<WebSocketTestServer> StartAsync(
        WebSocketTestProtocol protocol,
        Action<WebSocketOptions>? configure,
        Func<IHttpContext, Task> handler,
        CancellationToken cancellationToken)
    {
        TcpConnectionListener? tcpListener = null;
        InMemoryMultiplexedConnectionListener? http3Listener = null;

        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        if (protocol == WebSocketTestProtocol.Http3)
        {
            http3Listener = new InMemoryMultiplexedConnectionListener();
            builder.Server.UseServer(options => options.UseHttp3(http3Listener));
        }
        else
        {
            tcpListener = new TcpConnectionListener(new TcpConnectionListenerOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            });

            builder.Server.UseServer(options =>
            {
                if (protocol == WebSocketTestProtocol.Http2)
                {
                    options.UseHttp2(tcpListener);
                }
                else
                {
                    options.UseHttp1(tcpListener);
                }
            });
        }

        WebApplication application = builder.Build();
        application.UseWebSockets(configure);
        application.Use((context, next) => handler(context));

        await ((IWebApplication)application).StartAsync(cancellationToken);

        return new WebSocketTestServer(application, tcpListener, http3Listener);
    }

    public async ValueTask DisposeAsync()
    {
        await ((IWebApplication)_application).StopAsync(CancellationToken.None);
        await ((IAsyncDisposable)_application).DisposeAsync();
    }
}
