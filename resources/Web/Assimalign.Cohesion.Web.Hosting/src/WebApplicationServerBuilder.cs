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

            return new WebApplicationServer(pipeline, listener);
        });
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
