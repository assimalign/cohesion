#if NET7_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Transports;

public abstract class QuicServerTransportMiddleware : ITransportMiddleware
{

    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="next"></param>
    /// <returns></returns>
    public abstract Task InvokeAsync(QuicServerTransportContext context, TransportMiddlewareHandler next);

    Task ITransportMiddleware.InvokeAsync(ITransportContext context, TransportMiddlewareHandler next)
    {
        throw new NotImplementedException();
    }
}
#endif