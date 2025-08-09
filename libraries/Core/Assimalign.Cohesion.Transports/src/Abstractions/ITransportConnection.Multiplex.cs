using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Multiple logical streams sharing one physical transport.
/// </summary>
public interface IMultiplexTransportConnection : ITransportConnection
{
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    ITransportConnectionContext OpenInbound();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask<ITransportConnectionContext> OpenInboundAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    ITransportConnectionContext OpenOutbound();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask<ITransportConnectionContext> OpenOutboundAsync(CancellationToken cancellationToken = default);
}
