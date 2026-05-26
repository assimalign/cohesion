using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class MultiplexTransportConnection<TContext> : TransportConnection where TContext : TransportConnectionContext
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
}
