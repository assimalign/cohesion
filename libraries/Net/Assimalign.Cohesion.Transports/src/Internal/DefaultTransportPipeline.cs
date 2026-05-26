using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports.Internal;

internal class DefaultTransportPipeline<TContext> : TransportPipeline<TContext> where TContext : TransportConnectionContext
{
    private readonly TransportMiddleware _middleware;

    public DefaultTransportPipeline(TransportMiddleware middleware)
    {
        _middleware = middleware;
    }
    public override Task ExecuteAsync(TContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.Register(() => context.Cancel());

        return _middleware.Invoke(context);
    }
}
