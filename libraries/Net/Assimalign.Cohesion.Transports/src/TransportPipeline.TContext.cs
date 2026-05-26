using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class TransportPipeline<TContext> : TransportPipeline where TContext : TransportConnectionContext
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task ExecuteAsync(TContext context, CancellationToken cancellationToken = default);

    public sealed override Task ExecuteAsync(TransportConnectionContext context, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(ArgumentException.ThrowIfNotOfType<TContext>(context, nameof(context)), cancellationToken);
    }
}
