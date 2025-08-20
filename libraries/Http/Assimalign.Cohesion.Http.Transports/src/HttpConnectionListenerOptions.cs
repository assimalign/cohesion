using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Http;

using Transports;
using Cohesion.Internal;
using System.Net;

public sealed class HttpConnectionListenerOptions
{
    private readonly IList<ITransport> _transports = new List<ITransport>();

    public HttpConnectionListenerOptions()
    {

    }

    /// <summary>
    /// 
    /// </summary>
    public HttpProtocol Protocols { get; set; } = HttpProtocol.Http1;

    /// <summary>
    /// A list of transports 
    /// </summary>
    public IReadOnlyCollection<ITransport> Transports => _transports.AsReadOnly();

    public HttpConnectionListenerOptions ListenOn(EndPoint endpoint, int port)
    {

    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public HttpConnectionListenerOptions UseHttp1(Action<TcpServerTransportOptions> configure)
    {
        var transport = TcpServerTransport.Create(configure);


        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public HttpConnectionListenerOptions UseHttp2(Action<TcpServerTransportOptions> configure)
    {
        var transport = TcpServerTransport.Create(configure);


        return this;
    }
}
