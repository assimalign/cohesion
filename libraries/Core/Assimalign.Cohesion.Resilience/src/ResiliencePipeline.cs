using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

using Internal;

public sealed partial class ResiliencePipeline : IResiliencePipeline
{
    private readonly ResilienceStrategy _strategy;
    private readonly ResilienceContextPool _pool = ResilienceContextPool.Shared;

    internal ResiliencePipeline(ResilienceStrategy strategy)
    {
        _strategy = strategy;
    }

    public ValueTask ExecuteAsync(
        ResilienceCallback callback,
        object? state)
    {
        ResilienceContext context = _pool.Rent(false);
        try
        {
            return (this as IResiliencePipeline).ExecuteAsync(
                callback,
                context,
                state);
        }
        finally
        {
            _pool.Return(context);
        }
    }

    async ValueTask IResiliencePipeline.ExecuteAsync(
        ResilienceCallback callback,
        IResilienceContext context,
        object? state)
    {
        Outcome outcome = await _strategy
            .Invoke(callback, context, state)
            .ConfigureAwait(context.ContinueOnCapturedContext);
        
        // Bubble up exception
        outcome.ThrowIfException();
    }
}