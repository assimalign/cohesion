using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.Transports;

public abstract class TransportConnectionContext : ITransportConnectionContext
{
    private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

    protected TransportConnectionContext()
    {
        //ConnectionCancelled.Register()
    }

    /// <summary>
    /// The local endpoint the connection is bound to.
    /// </summary>
    public abstract EndPoint LocalEndPoint { get; }

    /// <summary>
    /// The remote endpoint the connection is bound to.
    /// </summary>
    public abstract EndPoint RemoteEndPoint { get; }

    /// <summary>
    /// A pipe to send and receive data from either client or server.
    /// </summary>
    public abstract ITransportConnectionPipe Pipe { get; }

    /// <summary>
    /// 
    /// </summary>
    public virtual CancellationToken ConnectionCancelled { get; }

    /// <summary>
    /// 
    /// </summary>
    public virtual IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();


    public void Cancel()
    {

    }
}
