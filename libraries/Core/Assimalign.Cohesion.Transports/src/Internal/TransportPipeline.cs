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
    public Task ExecuteAsync(ITransportConnection connection, ITransportConnectionContext context, CancellationToken cancellationToken = default)
    {
        return _middleware.Invoke(connection, context);
    }
}
