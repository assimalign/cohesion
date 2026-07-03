using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Hosting;


public sealed class WebApplicationServerBuilder
{
    private readonly WebApplicationBuilder _builder;
    private readonly List<Action<IServiceProvider, HttpConnectionListenerOptions>> _configurations = new();

    // Null == unlimited (the default). Captured here at builder time and read by the default
    // server's factory below; DI/Config integration for the Web server stays builder-time only.
    private int? _maxConcurrentConnections;

    internal WebApplicationServerBuilder(WebApplicationBuilder builder)
    {
        _builder = builder;
        _builder.Services.AddSingleton<IWebApplicationServer>(serviceProvider =>
        {
            IHttpConnectionListener listener = HttpConnectionListener.Create(options =>
            {
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
    /// 
    /// </summary>
    /// <param name="server"></param>
    /// <returns></returns>
    public WebApplicationServerBuilder UseServer<TServer>(TServer server) where TServer : IWebApplicationServer, IHostService
    {
        ArgumentNullException.ThrowIfNull(server);
        _builder.Services.AddSingleton<IHostService>(server);
        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="factory"></param>
    /// <returns></returns>
    public WebApplicationServerBuilder UseServer<TServer>(Func<IServiceProvider, TServer> factory) where TServer : IWebApplicationServer, IHostService
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
    /// Configures the default web server.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public WebApplicationServerBuilder UseServer(Action<HttpConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return UseServer((_, options) => configure.Invoke(options));
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public WebApplicationServerBuilder UseServer(Action<IServiceProvider, HttpConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _configurations.Add(configure);

        return this;
    }
}
