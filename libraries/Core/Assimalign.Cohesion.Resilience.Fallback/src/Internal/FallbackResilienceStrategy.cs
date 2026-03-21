using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal sealed class FallbackResilienceStrategy : IResilienceStrategy
{
    public FallbackResilienceStrategy(FallbackStrategyOptions options)
    {
        ShouldHandle = options.ShouldHandle;
        FallbackAction = options.FallbackAction;
    }

    private Func<FallbackPredicateArguments, ValueTask<bool>> ShouldHandle { get; }

    private Func<FallbackActionArguments, ValueTask>? FallbackAction { get; }

    public async ValueTask<Outcome> ExecuteAsync(
        ResilienceCallback callback,
        IResilienceContext context,
        object? state)
    {
        Outcome outcome = Outcome.Success;

        try
        {
            await callback.Invoke(context, state).ConfigureAwait(context.ContinueOnCapturedContext);
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
            .Invoke(new FallbackPredicateArguments(context, outcome))
            .ConfigureAwait(context.ContinueOnCapturedContext);

        if (!shouldHandle)
        {
            return outcome;
        }

        if (FallbackAction is null)
        {
            return Outcome.Success;
        }

        try
        {
            await FallbackAction
                .Invoke(new FallbackActionArguments(context, outcome))
                .ConfigureAwait(context.ContinueOnCapturedContext);

            return Outcome.Success;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
