using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

/// <summary>
/// 
/// </summary>
public abstract class TcpServerTransportMiddleware : ITransportMiddleware
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="next"></param>
    /// <returns></returns>
    public abstract Task InvokeAsync(TcpServerTransportContext context, TransportMiddlewareHandler next);

    Task ITransportMiddleware.InvokeAsync(ITransportContext context, TransportMiddlewareHandler next)
    {
        return context is TcpServerTransportContext tcpContext ? 
            InvokeAsync(tcpContext, next) :
            Task.CompletedTask;
    }
}