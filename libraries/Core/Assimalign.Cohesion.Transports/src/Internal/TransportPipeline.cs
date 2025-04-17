using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports.Internal;

internal class TransportPipeline : ITransportPipeline
{
    private readonly TransportMiddleware _middleware;
    public TransportPipeline(TransportMiddleware middleware)
    {
        _middleware = middleware;
    }

    public Task ExecuteAsync(ITransportContext context, CancellationToken cancellationToken = default)
    {
        // 
        return _middleware.Invoke(context);
    }
}
