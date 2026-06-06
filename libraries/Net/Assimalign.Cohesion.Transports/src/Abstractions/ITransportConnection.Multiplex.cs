using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

/// <summary>
/// Multiple logical streams sharing one physical transport.
/// </summary>
public interface IMultiplexTransportConnection : ITransportConnection
{
    /// <summary>
    /// Opens the inbound point to point connection.
    /// </summary>
    /// <returns></returns>
    IMultiplexTransportConnectionContext OpenInbound();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask<IMultiplexTransportConnectionContext> OpenInboundAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IMultiplexTransportConnectionContext OpenOutbound();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask<IMultiplexTransportConnectionContext> OpenOutboundAsync(CancellationToken cancellationToken = default);
}
