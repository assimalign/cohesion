using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class MultiplexTransportConnection : TransportConnection
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
}
