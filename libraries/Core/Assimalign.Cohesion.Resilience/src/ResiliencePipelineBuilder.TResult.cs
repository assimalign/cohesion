using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Resilience;

using Internal;

public sealed class ResiliencePipelineBuilder<TResult> : IResiliencePipelineBuilder<TResult>
{
    private readonly List<ResilienceStrategy<TResult>> _strategies;

    public ResiliencePipelineBuilder()
    {
        _strategies = new List<ResilienceStrategy<TResult>>();
    }


    public ResiliencePipelineBuilder<TResult> UseStrategy(ResilienceStrategy<TResult> strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies.Add(strategy);
        return this;
    }

    public ResiliencePipeline<TResult> Build()
    {
        if (_strategies.Count == 0)
        {
            // TODO: add a null strategy
        }

        return new ResiliencePipeline<TResult>(new CompositeStrategy<TResult>(_strategies));
    }

    IResiliencePipelineBuilder<TResult> IResiliencePipelineBuilder<TResult>.UseStrategy(IResilienceStrategy<TResult> strategy)
    {
        throw new NotImplementedException();
    }

    IResiliencePipeline<TResult> IResiliencePipelineBuilder<TResult>.Build()
    {
        return Build();
    }
}
