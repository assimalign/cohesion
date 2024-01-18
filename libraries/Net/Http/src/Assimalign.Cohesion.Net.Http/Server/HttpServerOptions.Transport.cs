using System;
using System.Collections.Generic;


namespace Assimalign.Cohesion.Net.Http;

using Assimalign.Cohesion.Net.Transports;

public partial class HttpServerOptions
{
    internal IList<ITransport> Transports => this.transports;


    /// <summary>
    /// Overrides the underlying HTTP Transports used for processing the connection.
    /// </summary>
    /// <param name="transport"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void AddTransport(ITransport transport)
    {
        ValidateTransport(transport);
        this.transports.Add(transport);
    }
    
    /// <summary>
    /// Overrides the underlying HTTP Transports used for processing the connection.
    /// </summary>
    /// <param name="configure"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void AddTransport(Func<ITransport> configure)
    {
        var transport = configure.Invoke();
        ValidateTransport(transport);
        this.transports.Add(transport);
    }


    /// <summary>
    /// Configures and adds the underlying TCP Transports that is used for HTTP/1.1 and HTTP/2.0
    /// </summary>
    /// <param name="configure"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void AddTcpTransport(Action<TcpServerTransportOptions> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var options = new TcpServerTransportOptions();

        configure.Invoke(options);
        
        this.transports.Add(new TcpServerTransport(options));
    }


    private void ValidateTransport(ITransport transport)
    {
        if (transport is null)
        {
            throw new ArgumentNullException(nameof(transport));
        }
        if (transport.TransportType == TransportType.Client)
        {
            throw new ArgumentException("Transport must be a server configure.", nameof(transport));
        }
        if (transport.ProtocolType != ProtocolType.Tcp && transport.ProtocolType != ProtocolType.Quic)
        {
            throw new ArgumentException("Transport must be a TCP or QUIC configure.", nameof(transport));
        }
    }
}
