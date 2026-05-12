using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Transports;
using Assimalign.Cohesion.Http.Transports;
using Assimalign.Cohesion.Web.Hosting.Internal;

public sealed class WebApplicationServerManager
{
    private readonly WebApplicationBuilder _builder;
    private readonly HttpConnectionListenerOptions _options;

    internal WebApplicationServerManager(WebApplicationBuilder builder)
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
    public WebApplicationServerManager UseServer(IWebApplicationServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        _builder.Services.AddSingleton<IWebApplicationServer>(new WebApplicationServerWrapper(server));

        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="factory"></param>
    /// <returns></returns>
    public WebApplicationServerManager UseServer(Func<IServiceProvider, IWebApplicationServer> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _builder.Services.AddSingleton<IWebApplicationServer>(serviceProvider =>
        {
            IWebApplicationServer server = factory.Invoke(serviceProvider);

            ArgumentNullException.ThrowIfNull(server);

            return new WebApplicationServerWrapper(server);
        });

        return this;
    }

    /// <summary>
    /// Configures the default web server.
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public WebApplicationServerManager ConfigureServer(Action<WebApplicationServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WebApplicationServerOptions(_options);

        configure.Invoke(options);

        return this;
    }
}
