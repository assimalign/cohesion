using System;
using System.Threading;

namespace Assimalign.Cohesion.Resilience.Internal;

internal abstract partial class CancellationTokenSourcePool
{
    private sealed class PooledCancellationTokenSourcePool : CancellationTokenSourcePool
    {
        public static readonly PooledCancellationTokenSourcePool SystemInstance = new(TimeProvider.System);

        private readonly ObjectPool<CancellationTokenSource> _pool;

        public PooledCancellationTokenSourcePool(TimeProvider timeProvider) => _pool = new(
            () =>
            {
                return new CancellationTokenSource(System.Threading.Timeout.InfiniteTimeSpan, timeProvider);
            },
            static cts => true);

        protected override CancellationTokenSource GetCore(TimeSpan delay)
        {
            var source = _pool.Rent();

            if (IsCancellable(delay))
            {
                source.CancelAfter(delay);
            }

            return source;
        }

        public override void Return(CancellationTokenSource source)
        {
            if (source.TryReset())
            {
                _pool.Return(source);
            }
            else
            {
                source.Dispose();
            }
        }
    }
}
