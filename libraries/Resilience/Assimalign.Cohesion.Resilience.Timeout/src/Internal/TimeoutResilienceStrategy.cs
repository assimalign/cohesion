using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class TimeoutResilienceStrategy : TimeoutResilienceStrategyBase, IResilienceStrategy
{
    public TimeoutResilienceStrategy(TimeoutStrategyOptions options) 
        : base(options) { }

    public ValueTask<Outcome> ExecuteAsync(ResilienceCallback callback, IResilienceContext context, object? state)
    {
        return InvokeAsync<Outcome>(
            async (context1, state1) =>
            {
                Outcome outcome = Outcome.Success;

                try
                {
                    await callback.Invoke(context1, state1).ConfigureAwait(context1.ContinueOnCapturedContext);
                }
                catch (Exception exception)
                {
                    outcome = exception;
                }

                return outcome;
            },
            (exception) => exception,
            context,
            state);
    }

    protected override bool IsOutcomeOperationCancelledException<TOutcome>(TOutcome outcome, out OperationCanceledException exception)
    {
        exception = default!;
        return outcome is Outcome o && o.IsFailure<OperationCanceledException>(out exception!);
    }
}