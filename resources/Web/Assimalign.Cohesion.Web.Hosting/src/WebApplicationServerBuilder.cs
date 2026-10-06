using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Configuration;
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

    // The Limits:MaxConcurrentConnections a configuration binding read. It is set while the default
    // server's factory runs the listener configurations, just before the factory reads it, and an
    // explicit LimitConcurrentConnections call takes precedence over it.
    private int? _configuredMaxConcurrentConnections;

    internal void OwnEndpointCertificate(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) => _builder.OwnEndpointCertificate(certificate);

    /// <summary>
    /// Gets the application's content root, against which a relative configured certificate path
    /// resolves.
    /// </summary>
    internal string? ContentRootPath => _builder.Environment.ContentRootPath?.ToString();

    /// <summary>
    /// Records the connection cap a configuration binding read. An explicit
    /// <see cref="LimitConcurrentConnections(int)"/> call takes precedence.
    /// </summary>
    /// <param name="maxConcurrentConnections">The configured cap; the binder has already checked it is positive.</param>
    internal void UseConfiguredConnectionLimit(int maxConcurrentConnections) => _configuredMaxConcurrentConnections = maxConcurrentConnections;

    internal WebApplicationServerBuilder(WebApplicationBuilder builder)
    {
        _builder = builder;

        // The default server is the first IWebApplicationServer registration, made when the
        // application builder is constructed, so it precedes every AddServer/UseServer server in
        // lifecycle order. It is inert when no listener was configured through this builder: a
        // custom-only composition must not also start an empty default listener.
        _builder.Services.AddSingleton<IWebApplicationServer>(serviceProvider =>
        {
            if (_configurations.Count == 0)
            {
                return new InactiveWebApplicationServer();
            }

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
                MaxConcurrentConnections = _maxConcurrentConnections ?? _configuredMaxConcurrentConnections
            });
        });
    }

    /// <summary>
    /// Caps the number of connections the default server serves concurrently.
    /// </summary>
    /// <remarks>
    /// By default the server is unlimited. When a cap is set, the accept loop reserves a slot
    /// before accepting each connection, so once the cap is reached additional connections are left
    /// in the listener backlog — accepted but not opened or served — until an active connection
    /// completes and frees a slot. The cap can also come from configuration
    /// (<c>Http:Limits:MaxConcurrentConnections</c>, bound by <c>UseConfiguration</c> and by an entry
    /// point's default endpoints); a cap set here takes precedence over a configured one.
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

        _builder.Services.AddSingleton<IWebApplicationServer>(server);
        return this;
    }

    /// <summary>
    /// Adds a custom server created from the application's service provider.
    /// </summary>
    /// <remarks>
    /// The factory is invoked once, at host start, and the application owns and disposes the
    /// server it returns.
    /// </remarks>
    /// <typeparam name="TServer">The custom server type.</typeparam>
    /// <param name="factory">The factory that creates the server.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is <see langword="null"/>.</exception>
    public WebApplicationServerBuilder UseServer<TServer>(Func<IServiceProvider, TServer> factory)
        where TServer : IWebApplicationServer, IHostService
    {
        ArgumentNullException.ThrowIfNull(factory);

        _builder.Services.AddSingleton<IWebApplicationServer>(serviceProvider =>
        {
            TServer server = factory.Invoke(serviceProvider);

            return server is null
                ? throw new InvalidOperationException("The web application server factory returned null.")
                : server;
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
    /// Gets whether the default server has at least one listener configuration.
    /// </summary>
    internal bool HasListenerConfiguration => _configurations.Count > 0;

    /// <summary>
    /// Configures the default server from the <c>Http</c> configuration section, binding
    /// <paramref name="developmentEndPoint"/> when the section declares no endpoint. The
    /// configuration is read when the server is created at host start, so sources added after
    /// this call still apply.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="developmentEndPoint">The endpoint bound when no endpoint is configured.</param>
    internal void UseDefaultEndpoints(IConfiguration configuration, IPEndPoint developmentEndPoint)
    {
        UseServer((_, options) => HttpServerConfiguration.BindOrDefault(
            configuration,
            HttpServerConfiguration.DefaultSectionKey,
            options,
            developmentEndPoint,
            OwnEndpointCertificate,
            ContentRootPath,
            UseConfiguredConnectionLimit));
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
}
