#if NET7_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

public abstract class QuicClientTransportMiddleware : ITransportMiddleware
{
    public abstract Task InvokeAsync(QuicClientTransportContext context, TransportMiddlewareHandler next);

    Task ITransportMiddleware.InvokeAsync(ITransportContext context, TransportMiddlewareHandler next)
    {
        return context is QuicClientTransportContext clientContext ?
            InvokeAsync(clientContext, next) :
            Task.CompletedTask;
    }
}
#endif