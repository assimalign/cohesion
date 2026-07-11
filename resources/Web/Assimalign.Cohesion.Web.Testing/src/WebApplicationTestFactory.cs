using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Testing.Internal;

namespace Assimalign.Cohesion.Web.Testing;

/// <summary>
/// Composes a <see cref="WebApplication"/> whose default server listens on the in-memory
/// connection driver — no sockets, no ports — and creates <see cref="HttpClient"/> instances
/// wired through <see cref="SocketsHttpHandler.ConnectCallback"/> to dial that listener, so
/// every request flows the application's full pipeline (middleware, routing, features) end
/// to end.
/// </summary>
/// <remarks>
/// <para>
/// The intended flow is: construct the factory, configure services on <see cref="Builder"/>,
/// configure the request pipeline on <see cref="Application"/>, then call
/// <see cref="CreateClient"/> (which starts the server on first use) and send requests.
/// The pipeline snapshot is taken when the server starts, so all
/// <c>Use</c>/<c>UseRouting</c>/<c>Map</c> calls must happen before the factory starts
/// serving.
/// </para>
/// <para>
/// Each factory owns a private <see cref="InMemoryConnectionListener"/> and a private
/// application instance (including per-application router state), so multiple factories in
/// one process are fully isolated and safe to run in parallel. Disposal stops the server —
/// draining in-flight connections per the Web.Hosting stop semantics — and tears down the
/// in-memory transport.
/// </para>
/// <para>
/// The factory is AOT/trim-safe: composition is plain delegate wiring over the builder
/// seams, and the client side rides BCL types (<see cref="SocketsHttpHandler"/> over a
/// duplex-pipe stream). No reflection anywhere.
/// </para>
/// </remarks>
public sealed class WebApplicationTestFactory : IWebApplicationTestFactory
{
    private readonly WebApplicationTestFactoryOptions _options;
    private readonly InMemoryConnectionListener _listener;
    private readonly InMemoryConnectionFactory _connectionFactory;
    private readonly Lock _gate = new();

    private WebApplication? _application;
    private IWebApplicationServer? _server;
    private bool _isStarted;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new factory serving HTTP/1.1 over the in-memory transport.
    /// </summary>
    public WebApplicationTestFactory() : this(new WebApplicationTestFactoryOptions())
    {
    }

    /// <summary>
    /// Initializes a new factory with the supplied options.
    /// </summary>
    /// <param name="options">The factory options (protocol, base address).</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="options"/> or its <see cref="WebApplicationTestFactoryOptions.BaseAddress"/>
    /// is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="WebApplicationTestFactoryOptions.Protocol"/> is not a defined
    /// <see cref="WebApplicationTestProtocol"/> value.
    /// </exception>
    public WebApplicationTestFactory(WebApplicationTestFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.BaseAddress);

        if (options.Protocol is not (WebApplicationTestProtocol.Http1 or WebApplicationTestProtocol.Http2))
        {
            throw new ArgumentException(
                $"'{options.Protocol}' is not a supported test protocol. HTTP/3 is out of scope for the in-memory factory (see docs/DESIGN.md).",
                nameof(options));
        }

        _options = options;
        _listener = new InMemoryConnectionListener();
        _connectionFactory = _listener.CreateFactory();

        Builder = WebApplication.CreateBuilder();
        Builder.Server.UseServer(listenerOptions =>
        {
            if (_options.Protocol == WebApplicationTestProtocol.Http2)
            {
                listenerOptions.UseHttp2(_listener);
            }
            else
            {
                listenerOptions.UseHttp1(_listener);
            }
        });
    }

    /// <summary>
    /// Gets the application builder, for service/configuration registration before the
    /// application is built (for example <c>Builder.AddRouting()</c> or additional
    /// <c>Builder.Server.UseServer(...)</c> listener configuration).
    /// </summary>
    public WebApplicationBuilder Builder { get; }

    /// <summary>
    /// Gets the web application under test, building it on first access. Configure the
    /// request pipeline (<c>Use</c>, <c>UseRouting</c>, <c>Map</c>) through this member
    /// before the factory starts serving — the pipeline snapshot is taken at server start.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the factory has been disposed.</exception>
    public WebApplication Application
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);

                return _application ??= Builder.Build();
            }
        }
    }

    /// <inheritdoc />
    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _isStarted;
            }
        }
    }

    IWebApplication IWebApplicationTestFactory.Application => Application;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        IWebApplicationServer server;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            _application ??= Builder.Build();

            // Resolving the server materializes the pipeline snapshot; the resolved instance
            // is the default server the constructor wired onto the in-memory listener. The
            // server's own start is idempotent, so a concurrent double-start is safe.
            _server ??= _application.Context.ServiceProvider.GetRequiredService<IWebApplicationServer>();
            _isStarted = true;

            server = _server;
        }

        return server.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IWebApplicationServer? server;

        lock (_gate)
        {
            server = _isStarted ? _server : null;
        }

        return server?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// When the factory has not started yet, this member starts it first. The default
    /// server's start only schedules its accept loop and completes synchronously, so the
    /// blocking wait here cannot deadlock; it keeps <see cref="CreateClient"/> a one-call
    /// entry point.
    /// </remarks>
    public HttpClient CreateClient()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
        }

        if (!IsStarted)
        {
            StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        SocketsHttpHandler handler = new()
        {
            ConnectCallback = (_, cancellationToken) => DialAsync(cancellationToken),
        };

        HttpClient client = new(handler, disposeHandler: true)
        {
            BaseAddress = _options.BaseAddress,
        };

        if (_options.Protocol == WebApplicationTestProtocol.Http2)
        {
            // Prior-knowledge HTTP/2 over the plaintext in-memory stream: an exact 2.0
            // version policy makes SocketsHttpHandler speak h2 from the first byte with no
            // TLS/ALPN negotiation and no Upgrade dance.
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        return client;
    }

    /// <summary>
    /// Stops the server when started (draining in-flight connections), disposes the
    /// application, and tears down the in-memory listener. Disposal is idempotent.
    /// </summary>
    /// <returns>A task that completes when the factory has fully torn down.</returns>
    public async ValueTask DisposeAsync()
    {
        IWebApplicationServer? server;
        WebApplication? application;

        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            server = _isStarted ? _server : null;
            application = _application;
        }

        if (server is not null)
        {
            // Graceful stop: stops accepting, drains in-flight connections, and disposes
            // the HTTP connection listener (which releases the transport listener).
            await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (application is not null)
        {
            // Stops any user-registered host services; a never-started host no-ops.
            await ((IAsyncDisposable)application).DisposeAsync().ConfigureAwait(false);
        }

        // Defensive teardown for the never-started path; the in-memory listener's disposal
        // is idempotent when the server already released it.
        await _listener.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<Stream> DialAsync(CancellationToken cancellationToken)
    {
        Connection connection = await _connectionFactory
            .ConnectAsync(_listener.EndPoint, cancellationToken)
            .ConfigureAwait(false);

        return new ClientConnectionStream(connection);
    }
}
