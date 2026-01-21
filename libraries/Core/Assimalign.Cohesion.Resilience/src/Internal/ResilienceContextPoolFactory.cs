using Assimalign.Cohesion.ObjectPool;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class ResilienceContextPoolFactory : ObjectPoolFactory<ResilienceContext, ResilienceContextCreationArguments>
{
    public override ResilienceContext Create(ResilienceContextCreationArguments args)
    {
        ResilienceContext context = new DefaultResilienceContext();

        context.TryInitialize(args);

        return context;
    }
}
