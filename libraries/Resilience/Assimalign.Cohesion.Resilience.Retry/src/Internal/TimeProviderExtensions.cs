using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal static class TimeProviderExtensions
{
    extension (TimeProvider timeProvider)
    {
        public Task DelayAsync(TimeSpan delay, IResilienceContext context)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentNullException.ThrowIfNull(context);

            context.CancellationToken.ThrowIfCancellationRequested();

            if (delay == TimeSpan.MaxValue)
            {
                delay = System.Threading.Timeout.InfiniteTimeSpan;
            }

            return Task.Delay(delay, timeProvider, context.CancellationToken);
        }
    }
}
