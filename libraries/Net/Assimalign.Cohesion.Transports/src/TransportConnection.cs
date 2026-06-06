using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;


public abstract class TransportConnection : ITransportConnection
{
    /// <inheritdoc />
    public abstract ConnectionId Id { get; }

    /// <inheritdoc />
    public abstract TransportId TransportId { get; }

    /// <inheritdoc />
    public abstract TransportProtocol Protocol { get; }

    /// <inheritdoc />
    public abstract ConnectionState State { get; }

    /// <inheritdoc />
    public abstract CancellationToken ConnectionAborted { get; }

    /// <summary>
    /// 
    /// </summary>
    protected virtual TransportPipeline? Pipeline { get; }

    /// <inheritdoc />
    public virtual void Abort() => AbortAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <inheritdoc />
    public abstract ValueTask AbortAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public virtual void Dispose() => DisposeAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();

}
