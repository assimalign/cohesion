using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class MultiplexTransportConnection<TContext> : TransportConnection, IMultiplexTransportConnection where TContext : MultiplexTransportConnectionContext
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

    IMultiplexTransportConnectionContext IMultiplexTransportConnection.OpenInbound()
    {
        return OpenInbound();
    }

    async ValueTask<IMultiplexTransportConnectionContext> IMultiplexTransportConnection.OpenInboundAsync(CancellationToken cancellationToken)
    {
        return await OpenInboundAsync(cancellationToken).ConfigureAwait(false);
    }

    IMultiplexTransportConnectionContext IMultiplexTransportConnection.OpenOutbound()
    {
        return OpenOutbound();
    }

    async ValueTask<IMultiplexTransportConnectionContext> IMultiplexTransportConnection.OpenOutboundAsync(CancellationToken cancellationToken)
    {
        return await OpenOutboundAsync(cancellationToken).ConfigureAwait(false);
    }
}
