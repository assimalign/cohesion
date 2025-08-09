using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Http;

using Transports;
using Cohesion.Internal;
using System.Net;

public sealed class HttpListenerOptions
{
    private readonly IList<ITransport> _transports = new List<ITransport>();

    public HttpListenerOptions()
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

    public HttpListenerOptions ListenOn(EndPoint endpoint, int port)
    {

    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public HttpListenerOptions UseHttp1(Action<TcpServerTransportOptions> configure)
    {
        var transport = TcpServerTransport.Create(configure);


        return this;
    }

    public HttpListenerOptions UseHttp2(Action<TcpServerTransportOptions> configure)
    {
        var transport = TcpServerTransport.Create(configure);


        return this;
    }
}
