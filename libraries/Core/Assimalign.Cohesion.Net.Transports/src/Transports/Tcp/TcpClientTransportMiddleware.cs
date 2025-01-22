using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

public abstract class TcpClientTransportMiddleware : ITransportMiddleware
{
    public abstract Task InvokeAsync(TcpClientTransportContext context, TransportMiddlewareHandler next);
    Task ITransportMiddleware.InvokeAsync(ITransportContext context, TransportMiddlewareHandler next)
    {
        return context is TcpClientTransportContext clientContext ?
            InvokeAsync(clientContext, next) :
            Task.CompletedTask;
    }
}


