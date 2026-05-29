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
    public virtual TransportConnectionContext OpenInbound() => OpenInboundAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract ValueTask<TransportConnectionContext> OpenInboundAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public virtual TransportConnectionContext OpenOutbound() => OpenOutboundAsync().ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract ValueTask<TransportConnectionContext> OpenOutboundAsync(CancellationToken cancellationToken = default);

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
