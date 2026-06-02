using System;
using System.Net;
using System.Threading;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Transports;

public abstract class TransportConnectionContext : ITransportConnectionContext
{
    private readonly CancellationTokenSource _cancellationTokenSource;

    protected TransportConnectionContext()
    {
        _cancellationTokenSource = new CancellationTokenSource();
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
    public virtual CancellationToken ConnectionCancelled => _cancellationTokenSource.Token;

    /// <summary>
    /// Gets a value indicating whether the stream this context represents is
    /// bidirectional. Defaults to <see langword="true"/>; multiplex transports
    /// that expose unidirectional streams (QUIC) override it.
    /// </summary>
    public virtual bool IsBidirectional => true;

    /// <summary>
    ///
    /// </summary>
    public virtual IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    /// <summary>
    /// 
    /// </summary>
    public virtual void Cancel()
    {
        _cancellationTokenSource.Cancel();
    }
}
