using Assimalign.Cohesion.Http;
using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Transports;
using Assimalign.Cohesion.Http.Transports;
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
    public WebApplicationServerBuilder UseServer(IWebApplicationServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var service = new WebApplicationServer(server);

        _builder.Services.AddSingleton<IWebApplicationServer>(service);
        _builder.Services.AddSingleton<IHostService>(service);

        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="factory"></param>
    /// <returns></returns>
    public WebApplicationServerBuilder UseServer(Func<IServiceProvider, IWebApplicationServer> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Func<IServiceProvider, WebApplicationServer> factory2 = serviceProvider =>
        {
            IWebApplicationServer server = factory.Invoke(serviceProvider);

            ArgumentNullException.ThrowIfNull(server);

            return new WebApplicationServer(server);
        };

        _builder.Services.AddSingleton<IWebApplicationServer>(factory2);
        _builder.Services.AddSingleton<IHostService>(factory2);

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
