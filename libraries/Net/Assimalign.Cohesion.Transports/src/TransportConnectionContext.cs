using System;
using System.Net;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class TransportConnectionContext : ITransportConnectionContext
{
    /// <inheritdoc />
    public abstract EndPoint LocalEndPoint { get; }

    /// <inheritdoc />
    public abstract EndPoint RemoteEndPoint { get; }

    /// <inheritdoc />
    public abstract ITransportConnectionPipe Pipe { get; }

    /// <inheritdoc />
    public virtual IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    /// <inheritdoc />
    public abstract CancellationToken ConnectionClosed { get; }

    /// <inheritdoc />
    public virtual void Close()
    {
        CloseAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public abstract ValueTask CloseAsync();
}
