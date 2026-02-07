namespace Assimalign.Cohesion.Resilience.Internal;

using ObjectPool;
using System.Threading;

internal class ResilienceContextPoolPolicy : ObjectPoolPolicy<ResilienceContext>
{
    public override bool CanReturn(ResilienceContext context)
    {
        context.OperationKey = default;
        context.CancellationToken = default(CancellationToken);
        context.ContinueOnCapturedContext = false;

        return true;
    }
}
