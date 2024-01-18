using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

/// <summary>
/// UDP Specific middleware.
/// </summary>
public abstract class UdpServerTransportMiddleware : ITransportMiddleware
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="next"></param>
    /// <returns></returns>
    public abstract Task InvokeAsync(UdpServerTransportContext context, TransportMiddlewareHandler next);

    Task ITransportMiddleware.InvokeAsync(ITransportContext context, TransportMiddlewareHandler next)
    {
        if (context is UdpServerTransportContext udpContext)
        {
            return InvokeAsync(udpContext, next);
        }
        return Task.CompletedTask;
    }
}
