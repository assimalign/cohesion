using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Internal;

/// <summary>
/// Configures the servers that participate in a web application's host lifecycle.
/// </summary>
public sealed class WebApplicationServerBuilder
{
    private readonly WebApplicationBuilder _builder;
    private readonly List<Action<IServiceProvider, HttpConnectionListenerOptions>> _configurations = new();

    // Null == unlimited (the default). Captured here at builder time and read by the default
    // server's factory below; DI/Config integration for the Web server stays builder-time only.
    private int? _maxConcurrentConnections;

    internal void OwnEndpointCertificate(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) => _builder.OwnEndpointCertificate(certificate);

    internal WebApplicationServerBuilder(WebApplicationBuilder builder)
    {
        _builder = builder;
        _builder.Services.AddSingleton<IWebApplicationServer>(serviceProvider =>
        {
            IHttpConnectionListener listener = HttpConnectionListener.Create(options =>
            {
                ApplyDefaultInterceptors(options);

                foreach (var action in _configurations)
                {
                    action.Invoke(serviceProvider, options);
                }
            });

            IWebApplicationPipeline pipeline = serviceProvider.GetRequiredService<IWebApplicationPipeline>();

            return new WebApplicationServer(new WebApplicationServerOptions
            {
                Pipeline = pipeline,
                Listener = listener,
                MaxConcurrentConnections = _maxConcurrentConnections
            });
        });
        _builder.Services.AddSingleton<IHostService>(serviceProvider =>
        {
            // The default server participates in host startup only when at least one listener was
            // configured through this builder. A custom-only server composition must not also try
            // to start an empty default listener, but keeping this descriptor in its original
            // position preserves host-service ordering when the default is configured.
            return _configurations.Count == 0
                ? new InactiveDefaultServerService()
                : (IHostService)serviceProvider.GetRequiredService<IWebApplicationServer>();
        });
    }

    /// <summary>
    /// Caps the number of connections the default server serves concurrently.
    /// </summary>
    /// <remarks>
    /// By default the server is unlimited. When a cap is set, the accept loop reserves a slot
    /// before accepting each connection, so once the cap is reached additional connections are left
    /// in the listener backlog — accepted but not opened or served — until an active connection
    /// completes and frees a slot.
    /// </remarks>
    /// <param name="maxConcurrentConnections">The maximum number of concurrently served connections. Must be greater than zero.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxConcurrentConnections"/> is less than one.</exception>
    public WebApplicationServerBuilder LimitConcurrentConnections(int maxConcurrentConnections)
    {
        if (maxConcurrentConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentConnections),
                maxConcurrentConnections,
                "The maximum concurrent connection count must be greater than zero.");
        }

        _maxConcurrentConnections = maxConcurrentConnections;

        return this;
    }

    /// <summary>
    /// Adds a preconstructed custom server to the web application's host lifecycle.
    /// </summary>
    /// <typeparam name="TServer">The custom server type.</typeparam>
    /// <param name="server">The server to start and stop with the application.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="server"/> is <see langword="null"/>.</exception>
    public WebApplicationServerBuilder UseServer<TServer>(TServer server)
        where TServer : IWebApplicationServer, IHostService
    {
        ArgumentNullException.ThrowIfNull(server);
        _builder.Services.AddSingleton<IHostService>(server);
        return this;
    }

    /// <summary>
    /// Adds a custom server created from the application's service provider.
    /// </summary>
    /// <typeparam name="TServer">The custom server type.</typeparam>
    /// <param name="factory">The factory that creates the server.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
    public WebApplicationServerBuilder UseServer<TServer>(Func<IServiceProvider, TServer> factory)
        where TServer : IWebApplicationServer, IHostService
    {
        ArgumentNullException.ThrowIfNull(factory);

        _builder.Services.AddSingleton<IHostService>(serviceProvider =>
        {
            IHostService service = factory.Invoke(serviceProvider);

            return service;
        });

        return this;
    }

    /// <summary>
    /// Configures the default web server's HTTP connection listener.
    /// </summary>
    /// <param name="configure">The listener configuration callback.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public WebApplicationServerBuilder UseServer(Action<HttpConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return UseServer((_, options) => configure.Invoke(options));
    }

    /// <summary>
    /// Configures the default web server's HTTP connection listener using application services.
    /// </summary>
    /// <param name="configure">The callback that receives the service provider and listener options.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public WebApplicationServerBuilder UseServer(Action<IServiceProvider, HttpConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _configurations.Add(configure);

        return this;
    }

    /// <summary>
    /// Installs the web host's default request-parse interceptors. Runs before any user
    /// configuration so the defaults occupy the front of the interceptor order: the
    /// max-request-body-size interceptor is registered first, guaranteeing every request
    /// carries the typed <c>IHttpMaxRequestBodySizeFeature</c> and that user-registered
    /// interceptors' <c>AfterRequestHead</c> hooks can observe it (all three protocol versions
    /// run the request-parse seam). User configurations may still inspect or clear
    /// <see cref="HttpConnectionListenerOptions.Interceptors"/> to opt out.
    /// </summary>
    /// <param name="options">The listener options being composed.</param>
    internal static void ApplyDefaultInterceptors(HttpConnectionListenerOptions options)
    {
        options.Interceptors.Add(HttpRequestLimits.CreateMaxRequestBodySizeInterceptor());
    }

    private sealed class InactiveDefaultServerService : IHostService
    {
        public ServiceId Id { get; } = ServiceId.New();

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
