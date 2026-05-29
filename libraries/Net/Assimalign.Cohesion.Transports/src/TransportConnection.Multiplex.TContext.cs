using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class MultiplexTransportConnection<TContext> : TransportConnection, IMultiplexTransportConnection where TContext : TransportConnectionContext
{
    /// <summary>
    /// Opens the inbound point to point connection.
    /// </summary>
    /// <returns></returns>
    public virtual TContext OpenInbound() => OpenInboundAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract ValueTask<TContext> OpenInboundAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public virtual TContext OpenOutbound() => OpenOutboundAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract ValueTask<TContext> OpenOutboundAsync(CancellationToken cancellationToken = default);

    ITransportConnectionContext IMultiplexTransportConnection.OpenInbound()
    {
        return OpenInbound();
    }

    async ValueTask<ITransportConnectionContext> IMultiplexTransportConnection.OpenInboundAsync(CancellationToken cancellationToken)
    {
        return await OpenInboundAsync(cancellationToken).ConfigureAwait(false);
    }

    ITransportConnectionContext IMultiplexTransportConnection.OpenOutbound()
    {
        return OpenOutbound();
    }

    async ValueTask<ITransportConnectionContext> IMultiplexTransportConnection.OpenOutboundAsync(CancellationToken cancellationToken)
    {
        return await OpenOutboundAsync(cancellationToken).ConfigureAwait(false);
    }
}
