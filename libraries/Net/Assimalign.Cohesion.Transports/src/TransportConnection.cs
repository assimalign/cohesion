using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;


public abstract class TransportConnection : ITransportConnection
{
    /// <summary>
    /// A unique connection id.
    /// </summary>
    public abstract ConnectionId Id { get; }

    /// <summary>
    /// Get the id of the transport in which the connection belongs to.
    /// </summary>
    public abstract TransportId TransportId { get; }

    /// <summary>
    /// The underlying network protocol of the transport connection.
    /// </summary>
    public abstract TransportProtocol Protocol { get; }

    /// <summary>
    /// Represents the current state of the pipeline.
    /// </summary>
    public abstract ConnectionState State { get; }

    /// <summary>
    /// 
    /// </summary>
    protected virtual TransportPipeline? Pipeline { get; }

    /// <summary>
    /// Aborts the connection.
    /// </summary>
    public virtual void Abort() => AbortAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// Asynchronously aborts the connection.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract ValueTask AbortAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual void Dispose() => DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

    /// <inheritdoc />
    ITransportPipeline ITransportConnection.Pipeline => Pipeline;
}
