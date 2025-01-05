using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

public abstract class UdpClientTransportMiddleware : ITransportMiddleware
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="next"></param>
    /// <returns></returns>
    public abstract Task InvokeAsync(UdpClientTransportContext context, TransportMiddlewareHandler next);

    Task ITransportMiddleware.InvokeAsync(ITransportContext context, TransportMiddlewareHandler next)
    {
        if (context is UdpClientTransportContext udpContext)
        {
            return InvokeAsync(udpContext, next);
        }
        return Task.CompletedTask;
    }
}
