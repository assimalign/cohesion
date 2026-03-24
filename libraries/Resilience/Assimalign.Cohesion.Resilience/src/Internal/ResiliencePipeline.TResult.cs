using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience.Internal;

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
}
