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
            async (c, s) =>
            {
                try
                {
                    await callback.Invoke(c, s).ConfigureAwait(c.ContinueOnCapturedContext);

                    return true;
                }
                catch (Exception exception)
                {
                    return exception;
                }
            },
            (exception) => exception,
            context,
            state);

    }

    protected override bool IsOutcomeOperationCancelledException<TOutcome>(TOutcome outcome, out OperationCanceledException exception)
    {
        exception = default!;
        if (outcome is Outcome o && !o.IsSuccess && ((Exception)o) is OperationCanceledException e)
        {
            exception = e;
            return true;
        }
        return false;
    }
}