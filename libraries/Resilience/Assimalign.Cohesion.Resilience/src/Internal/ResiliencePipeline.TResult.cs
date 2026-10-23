using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

[global::System.Diagnostics.DebuggerDisplay("ResiliencePipeline")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(ResiliencePipeline<>.DebugView))]
internal sealed class ResiliencePipeline<TResult> : IResiliencePipeline<TResult>
{
    private readonly ResilienceStrategy<TResult> _strategy;

    internal ResiliencePipeline(ResilienceStrategy<TResult> strategy)
    {
        _strategy = strategy;
    }

    async ValueTask<TResult> IResiliencePipeline<TResult>.ExecuteAsync(
       ResilienceCallback<TResult> callback,
       IResilienceContext context,
       object? state)
    {
        Outcome<TResult> outcome = await _strategy
            .Invoke(callback, context, state)
            .ConfigureAwait(context.ContinueOnCapturedContext);

        outcome.ThrowIfException();

        return (TResult)outcome;
    }

    private sealed class DebugView
    {
        public DebugView(ResiliencePipeline<TResult> value)
        {
        }
    }
}
