using System;

namespace Assimalign.Cohesion.Net.Transports;

using Assimalign.Cohesion.Net.Transports.Internal;

public sealed class TcpServerTransportContext : ITransportContext
{
    private readonly SocketTransportConnection connection;

    internal TcpServerTransportContext(SocketTransportConnection connection)
    {
        this.connection = connection;
    }
    /// <inheritdoc />
    public ITransportConnection Connection => this.connection;
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