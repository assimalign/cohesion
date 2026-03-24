using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal sealed class FallbackResilienceStrategy<TResult> : IResilienceStrategy<TResult>
{
    public FallbackResilienceStrategy(FallbackStrategyOptions<TResult> options)
    {
        ShouldHandle = options.ShouldHandle;
        FallbackAction = options.FallbackAction;
    }

    private Func<FallbackPredicateArguments<TResult>, ValueTask<bool>> ShouldHandle { get; }

    private Func<FallbackActionArguments<TResult>, ValueTask<TResult>> FallbackAction { get; }

    public async ValueTask<Outcome<TResult>> ExecuteAsync(
        ResilienceCallback<TResult> callback,
        IResilienceContext context,
        object? state)
    {
        Outcome<TResult> outcome;

        try
        {
            outcome = await callback.Invoke(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
        }
        catch (Exception exception)
        {
            outcome = exception;
        }

        if (!outcome.IsFailure())
        {
            return outcome;
        }

        bool shouldHandle = await ShouldHandle
            .Invoke(new FallbackPredicateArguments<TResult>(context, outcome))
            .ConfigureAwait(context.ContinueOnCapturedContext);

        if (!shouldHandle)
        {
            return outcome;
        }

        try
        {
            TResult fallback = await FallbackAction
                .Invoke(new FallbackActionArguments<TResult>(context, outcome))
                .ConfigureAwait(context.ContinueOnCapturedContext);

            return fallback;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
