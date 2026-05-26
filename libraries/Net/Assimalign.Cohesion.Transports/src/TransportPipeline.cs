using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Transports;

public abstract class TransportPipeline : ITransportPipeline
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public abstract Task ExecuteAsync(TransportConnectionContext context, CancellationToken cancellationToken = default);

    Task ITransportPipeline.ExecuteAsync(ITransportConnectionContext context, CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            ArgumentException.ThrowIfNotOfType<TransportConnectionContext>(context),
            cancellationToken);
    }
}
