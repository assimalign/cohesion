using System;
using System.Net;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Transports;

using Internal;

public sealed class TcpTransportConnectionContext : ITransportConnectionContext
{
    private readonly SocketTransportConnectionContext _context;

    internal TcpTransportConnectionContext(SocketTransportConnectionContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public EndPoint LocalEndPoint => _context.LocalEndPoint;

    /// <inheritdoc />
    public EndPoint RemoteEndPoint => _context.RemoteEndPoint;

    /// <inheritdoc />
    public ITransportConnectionPipe Pipe => _context.Pipe;

    /// <inheritdoc />
    public IDictionary<string, object?> Items => _context.Items;

    /// <summary>
    /// Sets the connection pipe of the existing connection.
    /// </summary>
    /// <param name="pipe"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void SetPipe(ITransportConnectionPipe pipe)
    {
        _context.Pipe = ArgumentNullException.ThrowIfNull<ITransportConnectionPipe>(pipe);
    }
}
