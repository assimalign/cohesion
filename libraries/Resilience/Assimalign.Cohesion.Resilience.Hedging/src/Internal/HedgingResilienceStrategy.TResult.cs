using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal sealed class HedgingResilienceStrategy<TResult> : HedgingResilienceStrategyBase, IResilienceStrategy<TResult>
{
    public HedgingResilienceStrategy(HedgingStrategyOptions options)
        : base(options)
    {
    }

    public ValueTask<Outcome<TResult>> ExecuteAsync(
        ResilienceCallback<TResult> callback,
        IResilienceContext context,
        object? state)
    {
        return ExecuteAsync<Outcome<TResult>>(
            async (c, s) =>
            {
                try
                {
                    return await callback.Invoke(c, s).ConfigureAwait(c.ContinueOnCapturedContext);
                }
                catch (Exception exception)
                {
                    return exception;
                }
            },
            static outcome => outcome.IsFailure(out Exception? exception) ? exception : null,
            static exception => exception,
            context,
            state);
    }
}
