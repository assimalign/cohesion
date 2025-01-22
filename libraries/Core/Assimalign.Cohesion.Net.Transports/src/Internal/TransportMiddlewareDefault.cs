using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports.Internal;

internal class TransportMiddlewareDefault : ITransportMiddleware
{
    private readonly TransportMiddleware middleware;

    public TransportMiddlewareDefault(TransportMiddleware middleware)
    {
        if (middleware is null)
        {
            throw new ArgumentNullException(nameof(middleware));    
        }
        this.middleware = middleware;    
    }

    public int SequenceId { get; }
    public Task InvokeAsync(ITransportContext context, TransportMiddlewareHandler next)
    {
        return middleware.Invoke(context, next);   
    }
}