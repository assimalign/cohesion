using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

internal class TimeoutResilienceStrategy<TResult> : TimeoutResilienceStrategyBase, IResilienceStrategy<TResult>
{
    public TimeoutResilienceStrategy(TimeoutStrategyOptions options)
        : base(options) { }

    public ValueTask<Outcome<TResult>> ExecuteAsync(ResilienceCallback<TResult> callback, IResilienceContext context, object? state)
    {
        return InvokeAsync<Outcome<TResult>>(
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
           (exception) => exception,
           context,
           state);
    }

    protected override bool IsOutcomeOperationCancelledException<TOutcome>(TOutcome outcome, out OperationCanceledException exception)
    {
        exception = default!;
        if (outcome is Outcome<TResult> o && !o.IsSuccess && ((Exception)o) is OperationCanceledException e)
        {
            exception = e;
            return true;
        }
        return false;
    }
}
