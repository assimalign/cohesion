using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

[global::System.Diagnostics.DebuggerDisplay("CircuitBreakerResilienceStrategy")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(CircuitBreakerResilienceStrategy<>.DebugView))]
internal sealed class CircuitBreakerResilienceStrategy<TResult> : CircuitBreakerResilienceStrategyBase, IResilienceStrategy<TResult>
{
    public CircuitBreakerResilienceStrategy(CircuitBreakerStrategyOptions options)
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

    private sealed class DebugView
    {
        public DebugView(CircuitBreakerResilienceStrategy<TResult> value)
        {
        }
    }
}
