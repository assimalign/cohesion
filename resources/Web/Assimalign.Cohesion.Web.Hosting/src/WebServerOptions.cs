using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Web.Hosting;

using Assimalign.Cohesion.Http.Transports;
using Assimalign.Cohesion.Transports;

public sealed class WebServerOptions
{
    private readonly HttpConnectionListenerOptions _options;

    internal WebServerOptions(HttpConnectionListenerOptions options)
    {
        _options = options;
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
