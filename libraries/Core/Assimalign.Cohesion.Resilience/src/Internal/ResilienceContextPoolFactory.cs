using Assimalign.Cohesion.ObjectPool;
using System.Threading;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class ResilienceContextPoolFactory : ObjectPoolFactory<ResilienceContext, ResilienceContextCreationArguments>
{
    public override ResilienceContext Create(ResilienceContextCreationArguments args)
    {
        ResilienceContext context = new ResilienceContext();

        context.OperationKey = args.OperationKey;
        context.CancellationToken = args.CancellationToken;
        context.ContinueOnCapturedContext = args.ContinueOnCapturedContext ?? false;

        return context;
    }
}
