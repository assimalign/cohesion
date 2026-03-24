using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal.Pipeline;

[DebuggerDisplay("{Strategy}")]
internal sealed class BridgeComponent<T> : BridgeComponentBase
{
    public BridgeComponent(ResilienceStrategy<T> strategy)
        : base(strategy) => Strategy = strategy;

    public ResilienceStrategy<T> Strategy { get; }

    internal override ValueTask<OutcomeO<TResult>> ExecuteAsync<TResult, TState>(
        Func<ResilienceContextO, TState, ValueTask<OutcomeO<TResult>>> callback,
        ResilienceContextO context,
        TState state)
    {
        // Check if we can cast directly, thus saving some cycles and improving the performance
        if (callback is Func<ResilienceContextO, TState, ValueTask<OutcomeO<T>>> casted)
        {
            var task = Strategy.ExecuteAsync(casted, context, state);

            // Using Unsafe.As avoids boxing allocations that would occur with a cast through object.
            Debug.Assert(task is ValueTask<OutcomeO<TResult>>, "Callback return type is identical to strategy return type");
            return Unsafe.As<ValueTask<OutcomeO<T>>, ValueTask<OutcomeO<TResult>>>(ref task);
        }
        else
        {
            var valueTask = Strategy.ExecuteAsync(
                static async (context, state) =>
                {
                    var outcome = await state.callback(context, state.state).ConfigureAwait(context.ContinueOnCapturedContext);
                    return ConvertOutcome<TResult, T>(outcome);
                },
                context,
                (callback, state));

            if (valueTask.IsCompletedSuccessfully)
            {
                return new ValueTask<OutcomeO<TResult>>(ConvertOutcome<T, TResult>(valueTask.Result));
            }

            return ConvertValueTaskAsync(valueTask, context);
        }

        static async ValueTask<OutcomeO<TResult>> ConvertValueTaskAsync(ValueTask<OutcomeO<T>> valueTask, ResilienceContextO resilienceContext)
        {
            var outcome = await valueTask.ConfigureAwait(resilienceContext.ContinueOnCapturedContext);
            return ConvertOutcome<T, TResult>(outcome);
        }
    }
}
