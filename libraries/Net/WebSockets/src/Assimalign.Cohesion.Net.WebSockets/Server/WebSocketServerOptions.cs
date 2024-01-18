using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.WebSockets;

using Assimalign.Cohesion.Net.Transports;

public sealed class WebSocketServerOptions
{
    

    public WebSocketServerOptions()
    {
        this.Transports = new List<ITransport>();
    }

    internal List<ITransport> Transports { get; }
    /// <summary>
    /// A user-friendly name for the server. This is represented in the 
    /// </summary>
    public string ServerName { get; set; } = "Cohesion.Net WebSocket Server";

    /// <summary>
    /// Overrides the underlying HTTP Transports used for processing the connection.
    /// </summary>
    /// <param name="transport"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void AddTransport(ITransport transport)
    {
        ValidateTransport(transport);
        this.Transports.Add(transport);
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
        this.Transports.Add(transport);
    }


    /// <summary>
    /// 
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

        this.Transports.Add(new TcpServerTransport(options));
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
        if (transport.ProtocolType != ProtocolType.Tcp && transport.ProtocolType != ProtocolType.Udp)
        {
            throw new ArgumentException("Transport must be a TCP or QUIC configure.", nameof(transport));
        }
    }
}
