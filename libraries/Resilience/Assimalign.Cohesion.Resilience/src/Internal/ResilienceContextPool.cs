using System.Threading;

namespace Assimalign.Cohesion.Resilience.Internal;

using ObjectPool;

internal class ResilienceContextPool : DefaultObjectPool<ResilienceContext, ResilienceContextCreationArguments>
{
    public ResilienceContextPool() 
        : base(new ResilienceContextPoolFactory(), new ResilienceContextPoolPolicy())
    {
    }

    public ResilienceContext Rent(CancellationToken cancellationToken = default)
        => Rent(default(OperationKey), cancellationToken);

    public ResilienceContext Rent(OperationKey operationKey, CancellationToken cancellationToken = default)
        => Rent(operationKey, null, cancellationToken);

    public ResilienceContext Rent(OperationKey operationKey, bool? continueOnCapturedContext, CancellationToken cancellationToken = default)
        => Rent(new ResilienceContextCreationArguments(operationKey, continueOnCapturedContext, cancellationToken));

    public ResilienceContext Rent(bool continueOnCapturedContext, CancellationToken cancellationToken = default)
        => Rent(new ResilienceContextCreationArguments(default, continueOnCapturedContext, cancellationToken));

    public override ResilienceContext Rent(ResilienceContextCreationArguments args)
    {
        ResilienceContext context = base.Rent(args);

        context.OperationKey = args.OperationKey;
        context.CancellationToken = args.CancellationToken;
        context.ContinueOnCapturedContext = args.ContinueOnCapturedContext ?? false;

        return context;
    }

    public static ResilienceContextPool Shared { get; } = new ResilienceContextPool();
}
