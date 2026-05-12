using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal sealed class RateLimitingResilienceStrategy : RateLimitingResilienceStrategyBase, IResilienceStrategy
{
    public RateLimitingResilienceStrategy(RateLimiterStrategyOptions options)
        : base(options)
    {
    }

    public ValueTask<Outcome> ExecuteAsync(
        ResilienceCallback callback,
        IResilienceContext context,
        object? state)
    {
        return ExecuteAsync<Outcome>(
            async (c, s) =>
            {
                try
                {
                    await callback.Invoke(c, s).ConfigureAwait(c.ContinueOnCapturedContext);
                    return Outcome.Success;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            },
            static exception => exception,
            context,
            state);
    }
}
