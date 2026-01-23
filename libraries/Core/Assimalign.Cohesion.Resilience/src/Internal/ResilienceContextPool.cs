using System.Threading;

namespace Assimalign.Cohesion.Resilience.Internal;

using Assimalign.Cohesion.ObjectPool.Internal;
using ObjectPool;

internal class ResilienceContextPool : DefaultObjectPool<ResilienceContext, ResilienceContextCreationArguments>
{
    public ResilienceContextPool() 
        : base(new ResilienceContextPoolFactory(), new ResilienceContextPoolPolicy())
    {
    }

    public ResilienceContext Rent(CancellationToken cancellationToken = default)
        => Rent(null, cancellationToken);

    public ResilienceContext Rent(OperationKey? operationKey, CancellationToken cancellationToken = default)
        => Rent(operationKey, null, cancellationToken);

    public ResilienceContext Rent(OperationKey? operationKey, bool? continueOnCapturedContext, CancellationToken cancellationToken = default)
        => Rent(new ResilienceContextCreationArguments(operationKey, continueOnCapturedContext, cancellationToken));

    public ResilienceContext Rent(bool continueOnCapturedContext, CancellationToken cancellationToken = default)
        => Rent(new ResilienceContextCreationArguments(null, continueOnCapturedContext, cancellationToken));

    public override ResilienceContext Rent(ResilienceContextCreationArguments arguments)
    {
        var context = base.Rent(arguments);
        context.TryInitialize(arguments);
        return context;
    }

    public static ResilienceContextPool Shared { get; } = new ResilienceContextPool();
}
