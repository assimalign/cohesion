using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Transports;
using Assimalign.Cohesion.Http.Transports;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Hosting;

public sealed class WebApplicationServerBuilder
{
    private readonly WebApplicationBuilder _builder;
    private readonly HttpConnectionListenerOptions _options;

    internal WebApplicationServerBuilder(WebApplicationBuilder builder)
    {
        _builder = builder;
        _options = new HttpConnectionListenerOptions();

        builder.Services.AddSingleton<IHttpConnectionListener>(serviceProvider =>
        {
            return new HttpConnectionListener(_options);
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
    public WebApplicationServerBuilder ConfigureServer(Action<WebServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WebServerOptions(_options);

        configure.Invoke(options);

        return this;
    }
}
