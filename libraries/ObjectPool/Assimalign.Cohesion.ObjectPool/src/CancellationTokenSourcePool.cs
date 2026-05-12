using System;
using System.Threading;

namespace Assimalign.Cohesion.ObjectPool;

public sealed class CancellationTokenSourcePool : DefaultObjectPool<CancellationTokenSource, TimeSpan>
{
    public CancellationTokenSourcePool() 
        : this(TimeProvider.System) { }

    public CancellationTokenSourcePool(TimeProvider timeProvider) 
        : base(new PoolFactory(timeProvider), new PoolPolicy()) { }


    public CancellationTokenSource Rent()
    {
        return Rent(Timeout.InfiniteTimeSpan);
    }

    public override CancellationTokenSource Rent(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Invalid delay specified.");
        }

        CancellationTokenSource cancellationTokenSource =  base.Rent(delay);

        if (delay != Timeout.InfiniteTimeSpan)
        {
            cancellationTokenSource.CancelAfter(delay);
        }

        return cancellationTokenSource;
    }

    private class PoolFactory : ObjectPoolFactory<CancellationTokenSource, TimeSpan>
    {
        private readonly TimeProvider _timeProvider;

        public PoolFactory() : this(TimeProvider.System)
        {

        }

        public PoolFactory(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        public sealed override CancellationTokenSource Create(TimeSpan delay)
        {
            return new CancellationTokenSource(Timeout.InfiniteTimeSpan, _timeProvider);
        }
    }
    private class PoolPolicy : ObjectPoolPolicy<CancellationTokenSource>
    {
        public override bool CanReturn(CancellationTokenSource instance)
        {
            bool canReturn = instance.TryReset();

            if (!canReturn)
            {
                instance.Dispose();
            }

            return canReturn;
        }
    }
}
