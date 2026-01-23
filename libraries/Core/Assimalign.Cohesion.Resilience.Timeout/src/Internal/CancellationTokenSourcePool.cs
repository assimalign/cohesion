using System;
using System.Threading;

namespace Assimalign.Cohesion.Resilience.Internal;

using ObjectPool;

internal class CancellationTokenSourcePool : DefaultObjectPool<CancellationTokenSource, TimeSpan>
{
    public CancellationTokenSourcePool(TimeProvider timeProvider) 
        : base(new CancellationTokenSourcePoolFactory(timeProvider), new CancellationTokenSourcePoolPolicy()) { }


    public override CancellationTokenSource Rent(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Invalid delay specified.");
        }

        CancellationTokenSource cancellationTokenSource =  base.Rent(delay);

        if (IsCancellable(delay))
        {
            cancellationTokenSource.CancelAfter(delay);
        }

        return cancellationTokenSource;
    }


    protected static bool IsCancellable(TimeSpan delay) => delay != Timeout.InfiniteTimeSpan;
}
