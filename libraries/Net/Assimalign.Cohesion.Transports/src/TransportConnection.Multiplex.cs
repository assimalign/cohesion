using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class MultiplexTransportConnection : TransportConnection, IMultiplexTransportConnection
{
    /// <summary>
    /// Opens the inbound point to point connection.
    /// </summary>
    /// <returns></returns>
    public virtual MultiplexTransportConnectionContext OpenInbound() => OpenInboundAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract ValueTask<MultiplexTransportConnectionContext> OpenInboundAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public virtual MultiplexTransportConnectionContext OpenOutbound() => OpenOutboundAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract ValueTask<MultiplexTransportConnectionContext> OpenOutboundAsync(CancellationToken cancellationToken = default);

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
