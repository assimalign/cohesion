using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Transports;
using Assimalign.Cohesion.Transports;

public sealed class WebApplicationServerOptions
{
    private readonly HttpConnectionListenerOptions _options;

    internal WebApplicationServerOptions(HttpConnectionListenerOptions options)
    {
        _options = options;
    }

    public Func<IHttpFeatureCollection>? CreateFeatures
    {
        get => _options.CreateFeatures;
        set => _options.CreateFeatures = value;
    }

    public void UseHttp1(Action<TcpServerTransportOptions> configure)
    {
        _options.UseHttp1(configure);
    }

    public void UseHttp2(Action<TcpServerTransportOptions> configure)
    {
        _options.UseHttp2(configure);
    }

    public void UseHttp3(Action<QuicServerTransportOptions> configure)
    {
        _options.UseHttp3(configure);
    }
}
