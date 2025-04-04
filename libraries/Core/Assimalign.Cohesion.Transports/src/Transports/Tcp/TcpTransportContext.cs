using System;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Transports.Internal;

public sealed class TcpTransportContext : ITransportContext
{
    private readonly SocketTransportConnection _connection;

    internal TcpTransportContext(SocketTransportConnection connection)
    {
        Connection = new TcpTransportConnection((_connection = connection));
    }

    /// <summary>
    /// 
    /// </summary>
    public TcpTransportConnection Connection { get; }
    ITransportConnection ITransportContext.Connection => Connection;

    public IServiceProvider? ServiceProvider { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    public void Trace(string message)
    {

    }


    /// <summary>
    /// Will ov
    /// </summary>
    /// <param name="pipe"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void SetPipe(ITransportConnectionPipe pipe)
    {
        _connection.Pipe = ThrowHelper.ThrowIfNull(pipe);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="data"></param>
    public void SetConnectionData(object? data)
    {
        _connection.ConnectionData = data;
    }
}
