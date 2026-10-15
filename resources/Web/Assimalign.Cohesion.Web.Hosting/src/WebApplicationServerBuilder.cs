using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Configuration;
using Assimalign.Cohesion.DependencyInjection;
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

    // The query parameters whose values the default server's request spans show in url.query, compared
    // as HttpQueryKey compares names. Null (the default) redacts every value; frozen when the server is
    // created.
    private HashSet<string>? _telemetryQueryValues;

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

            // The pipeline is resolved first: building it counts the application features it stamps
            // onto every exchange, which the listener needs to size each exchange's features.
            IWebApplicationPipeline pipeline = serviceProvider.GetRequiredService<IWebApplicationPipeline>();
            int featureCount = _builder.StampedFeatureCount;

            HttpConnectionListenerOptions options = new();
            ApplyDefaultInterceptors(options);
            options.ExchangeFeatureCapacity = featureCount;

            foreach (var action in _configurations)
            {
                action.Invoke(serviceProvider, options);
            }

            WebApplicationServerOptions serverOptions = new()
            {
                Pipeline = pipeline,
                Listener = options,
                MaxConcurrentConnections = _maxConcurrentConnections ?? _configuredMaxConcurrentConnections,
            };
            if (_telemetryQueryValues is not null)
            {
                serverOptions.TelemetryQueryValues.UnionWith(_telemetryQueryValues);
            }

            return new WebApplicationServer(serverOptions);
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
                ErrorMessages.ConcurrentConnectionCountInvalid);
        }

        _maxConcurrentConnections = maxConcurrentConnections;

        return this;
    }

    /// <summary>
    /// Declares query parameters whose values the default server's request spans may show.
    /// </summary>
    /// <remarks>
    /// The default server tags the span of every request that carries a query with <c>url.query</c>, as
    /// the OpenTelemetry HTTP server conventions require. Each parameter's value is replaced with
    /// <c>REDACTED</c> by default, because a query string can carry tokens, signatures and personal data
    /// that the server cannot recognize; the names stay, so the span still shows the query's shape. A
    /// parameter named here keeps its value. Names compare case-insensitively, as the request's query
    /// collection compares them, and repeated calls add to the names already declared. Name only
    /// parameters whose values are safe to send to a telemetry backend. Nothing is read or written when
    /// no activity listener subscribes to the server's source.
    /// </remarks>
    /// <param name="parameterNames">The names of the query parameters whose values are safe to show.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="parameterNames"/> or one of the names is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when one of the names is empty.</exception>
    public WebApplicationServerBuilder AllowQueryValuesInTelemetry(params string[] parameterNames)
    {
        ArgumentNullException.ThrowIfNull(parameterNames);

        // Every name is checked before any is kept, so a rejected call declares nothing.
        foreach (string parameterName in parameterNames)
        {
            ArgumentException.ThrowIfNullOrEmpty(parameterName, nameof(parameterNames));
        }

        _telemetryQueryValues ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _telemetryQueryValues.UnionWith(parameterNames);

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
        where TServer : IWebApplicationServer
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
        where TServer : IWebApplicationServer
    {
        ArgumentNullException.ThrowIfNull(factory);

        _builder.Services.AddSingleton<IWebApplicationServer>(serviceProvider =>
        {
            TServer server = factory.Invoke(serviceProvider);

            return server is null
                ? throw new InvalidOperationException(ErrorMessages.ApplicationServerFactoryResultNull)
                : server;
        });

        return this;
    }

    /// <summary>
    /// Configures the default web server's HTTP connection listener.
    /// </summary>
    /// <remarks>
    /// The hosting module's HTTP default interceptors and its
    /// <see cref="HttpConnectionListenerOptions.ExchangeFeatureCapacity"/> are set before
    /// <paramref name="configure"/> runs. The capacity counts the application features the pipeline stamps onto every exchange.
    /// The server adds its own feature allowance after configuration. A middleware that installs
    /// its own feature on every exchange, such as routing on a matched request or response compression,
    /// is not counted; add one slot for each (<c>options.ExchangeFeatureCapacity += 1</c>). After every
    /// configuration has run, the server rounds the capacity up to a size from which an exchange that
    /// carries more features still allocates no more than an unsized collection would.
    /// </remarks>
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
    /// <remarks>
    /// Runs in registration order after the server's defaults, as described for
    /// <see cref="UseServer(Action{HttpConnectionListenerOptions})"/>, including the rounding of
    /// <see cref="HttpConnectionListenerOptions.ExchangeFeatureCapacity"/> after the last configuration.
    /// </remarks>
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
    /// Installs HTTP policy defaults before application configuration. The server prepends its
    /// own client-fault and response-starting interceptors when it constructs the listener.
    /// </summary>
    /// <param name="options">The listener options being composed.</param>
    internal static void ApplyDefaultInterceptors(HttpConnectionListenerOptions options)
    {
        options.Interceptors.Add(new HttpRequestLimitsInterceptor());
        options.Interceptors.Add(new HttpProtocolUpgradeInterceptor());
        options.Interceptors.Add(new HttpExtendedConnectInterceptor());
    }
}
