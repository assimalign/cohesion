using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

[global::System.Diagnostics.DebuggerDisplay("CircuitBreakerResilienceStrategy")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class CircuitBreakerResilienceStrategy : CircuitBreakerResilienceStrategyBase, IResilienceStrategy
{
    public CircuitBreakerResilienceStrategy(CircuitBreakerStrategyOptions options)
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
            static outcome => outcome.IsFailure(out Exception? exception) ? exception : null,
            static exception => exception,
            context,
            state);
    }

    private sealed class DebugView
    {
        public DebugView(CircuitBreakerResilienceStrategy value)
        {
        }
    }
}
