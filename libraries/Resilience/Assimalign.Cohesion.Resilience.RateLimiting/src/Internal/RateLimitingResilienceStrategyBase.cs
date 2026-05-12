using System;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal abstract class RateLimitingResilienceStrategyBase
{
    protected RateLimitingResilienceStrategyBase(RateLimiterStrategyOptions options)
    {
        RateLimiter = options.RateLimiter ?? throw new InvalidOperationException("A rate limiter must be configured.");

        if (options.PermitCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PermitCount), options.PermitCount, "PermitCount must be greater than zero.");
        }

        PermitCount = options.PermitCount;
        OnRejected = options.OnRejected;
    }

    protected RateLimiter RateLimiter { get; }

    protected int PermitCount { get; }

    protected Func<OnRateLimiterRejectedArguments, ValueTask>? OnRejected { get; }

    protected async ValueTask<TOutcome> ExecuteAsync<TOutcome>(
        Func<IResilienceContext, object?, ValueTask<TOutcome>> callback,
        Func<RateLimiterRejectedException, TOutcome> error,
        IResilienceContext context,
        object? state)
    {
        using RateLimitLease lease = await RateLimiter
            .AcquireAsync(PermitCount, context.CancellationToken)
            .ConfigureAwait(context.ContinueOnCapturedContext);

        if (lease.IsAcquired)
        {
            return await callback.Invoke(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        TimeSpan? retryAfter = null;

        if (lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan delay))
        {
            retryAfter = delay;
        }

        OnRateLimiterRejectedArguments args = new(context, PermitCount, retryAfter);

        if (OnRejected is not null)
        {
            await OnRejected.Invoke(args).ConfigureAwait(context.ContinueOnCapturedContext);
        }

        return error.Invoke(new RateLimiterRejectedException(
            "The rate limiter rejected the execution.",
            retryAfter));
    }
}
