namespace Assimalign.Cohesion.Resilience.Internal;

using ObjectPool;

internal class ResilienceContextPoolPolicy : ObjectPoolPolicy<ResilienceContext>
{
    public override bool CanReturn(ResilienceContext instance)
    {
        return instance.TryReset();
    }
}
