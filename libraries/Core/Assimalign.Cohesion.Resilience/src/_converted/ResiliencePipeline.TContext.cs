using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

public abstract class ResiliencePipeline<TContext> : ResiliencePipeline
    where TContext : IResilienceContext
{
    protected ResiliencePipeline(ResilienceStrategy strategy) : base(strategy)
    {
    }


    public ValueTask<TResult> ExecuteAsync<TResult, TState>(
        ResiliencePipelineCallback<TResult, TState> callback,
        TContext context,
        TState state)
    {
        return base.ExecuteAsync(callback, context, state);
    }

}