using System;
using System.Net;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Transports;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.Transports.Internal;


public sealed class TcpTransportConnectionContext : ITransportConnectionContext
{
    private readonly SocketTransportConnectionContext _context;

    internal TcpTransportConnectionContext(SocketTransportConnectionContext context)
    {
        _context = context;
    }

   // public IServiceProvider? ServiceProvider { get; }

    public EndPoint LocalEndPoint => _context.LocalEndPoint;
    public EndPoint RemoteEndPoint => _context.RemoteEndPoint;
    public ITransportConnectionPipe Pipe => _context.Pipe;
    public IDictionary<string, object> Items => _context.Items;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="message"></param>
    public void Trace(string message)
    {
        _context.Trace.Invoke(null, Items, message);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="code"></param>
    /// <param name="message"></param>
    public void Trace(object? code, string message)
    {
        _context.Trace.Invoke(code, Items, message);
    }

    /// <summary>
    /// Sets the connection pipe of the existing connection.
    /// </summary>
    /// <param name="pipe"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public void SetPipe(ITransportConnectionPipe pipe)
    {
        _context.Pipe = ThrowHelper.ThrowIfNull(pipe);
    }
}
