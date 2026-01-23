using System.Threading;

namespace Assimalign.Cohesion.Resilience.Internal;

using ObjectPool;

internal class CancellationTokenSourcePoolPolicy : ObjectPoolPolicy<CancellationTokenSource>
{
    public override bool CanReturn(CancellationTokenSource instance)
    {
        return instance.TryReset();
    }
}
