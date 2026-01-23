using System;
using System.Threading;

namespace Assimalign.Cohesion.Resilience.Internal;

using ObjectPool;

internal class CancellationTokenSourcePoolFactory : ObjectPoolFactory<CancellationTokenSource, TimeSpan>
{
    private readonly TimeProvider _timeProvider;

    public CancellationTokenSourcePoolFactory(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public override CancellationTokenSource Create(TimeSpan delay)
    {
        return new CancellationTokenSource(Timeout.InfiniteTimeSpan, _timeProvider);
    }
}
