using System;
using System.Net;

namespace Assimalign.Cohesion.Transports;

public sealed class TcpTransportConnectionContext : TransportConnectionContext
{
    private ITransportConnectionPipe _pipe;

    internal TcpTransportConnectionContext(TransportConnectionPipe pipe, EndPoint localEndPoint, EndPoint remoteEndPoint)
    {
        _pipe = pipe;
        LocalEndPoint = localEndPoint;
        RemoteEndPoint = remoteEndPoint;
    }

    /// <inheritdoc />
    public override EndPoint LocalEndPoint { get; }

    /// <inheritdoc />
    public override EndPoint RemoteEndPoint { get; }

    /// <inheritdoc />
    public override ITransportConnectionPipe Pipe => _pipe;

    /// <summary>
    /// Sets the connection pipe of the existing connection.
    /// </summary>
    /// <param name="pipe"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void SetPipe(ITransportConnectionPipe pipe)
    {
        _pipe = ArgumentNullException.ThrowIfNull<ITransportConnectionPipe>(pipe);
    }
}
