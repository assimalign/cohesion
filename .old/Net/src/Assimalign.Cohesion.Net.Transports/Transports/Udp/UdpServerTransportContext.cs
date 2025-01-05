using System;
using System.Net;

namespace Assimalign.Cohesion.Net.Transports;

using Assimalign.Cohesion.Net.Transports.Internal;

public sealed class UdpServerTransportContext : ITransportContext
{
    private readonly SocketTransportConnection connection;

    internal UdpServerTransportContext(SocketTransportConnection connection)
    {
        this.connection = connection;
    }

    public ITransportConnection Connection => this.connection;

    /// <summary>
    /// Set's the remote endpoint in the connection.
    /// </summary>
    /// <param name="endpoint"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void SetRemoteEndPoint(EndPoint endpoint)
    {
        if (endpoint is null)
        {
            throw new ArgumentNullException(nameof(endpoint));
        }
        connection.RemoteEndPoint = endpoint;
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="pipe"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void SetPipe(ITransportConnectionPipe pipe)
    {
        if (pipe is null)
        {
            throw new ArgumentNullException(nameof(pipe));
        }
        connection.Pipe = pipe;
    }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="data"></param>
    public void SetConnectionData(object? data)
    {
        this.connection.ConnectionData = data;
    }
}
