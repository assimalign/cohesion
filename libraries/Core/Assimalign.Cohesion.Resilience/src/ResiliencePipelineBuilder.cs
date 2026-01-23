using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Resilience;

using Internal;

public sealed class ResiliencePipelineBuilder : IResiliencePipelineBuilder
{
    private readonly List<ResilienceStrategy> _strategies;

    public ResiliencePipelineBuilder()
    {
        _strategies = new List<ResilienceStrategy>();
    }

    public ResiliencePipelineBuilder UseStrategy(ResilienceStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies.Add(strategy);
        return this;
    }

    public ResiliencePipeline Build()
    {
        if (_strategies.Count == 0)
        {
            // TODO: add a null strategy
        }

        return new ResiliencePipeline(new CompositeStrategy(_strategies));
    }

    IResiliencePipelineBuilder IResiliencePipelineBuilder.UseStrategy(IResilienceStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        return UseStrategy(new StrategyWrapper(strategy));
    }

    IResiliencePipeline IResiliencePipelineBuilder.Build()
    {
        return Build();
    }
}
