using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Transports.Internal;

public sealed class TcpTransportConnection : ITransportConnection
{
    private readonly SocketTransportConnection _connection;

    internal TcpTransportConnection(SocketTransportConnection connection)
    {
        _connection = connection;
    }

    public bool IsConnected => _connection.IsConnected;
    public object? ConnectionData => _connection.ConnectionData;
    public ProtocolType Protocol => _connection.Protocol;
    public ConnectionState State => _connection.State;
    public ITransportConnectionPipe Pipe => _connection.Pipe;
    public EndPoint LocalEndPoint => _connection.LocalEndPoint;
    public EndPoint RemoteEndPoint => _connection.RemoteEndPoint;

    public void Abort()
    {
        _connection.Abort();
    }

    public ValueTask AbortAsync()
    {
        return _connection.AbortAsync();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    public void Execute()
    {
        _connection.Execute();
    }
}
