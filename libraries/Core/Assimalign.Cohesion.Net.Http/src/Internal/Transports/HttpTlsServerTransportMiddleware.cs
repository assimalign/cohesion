
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

using Assimalign.Cohesion.Net.Transports;

internal class HttpTlsServerTransportMiddleware : TcpServerTransportMiddleware
{
    public override Task InvokeAsync(TcpServerTransportContext context, TransportMiddlewareHandler next)
    {


        return Task.CompletedTask;
    }
}
