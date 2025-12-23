using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Resilience;

public sealed class ResiliencePipelineBuilder : IResiliencePipelineBuilder
{
    private readonly List<ResilienceStrategy> _strategies;

    public ResiliencePipelineBuilder()
    {
        
    }



    //public ResiliencePipelineBuilder AddStrategy()

    public ResiliencePipeline Build()
    {
        return default;
    }

    IResiliencePipelineBuilder IResiliencePipelineBuilder.AddStrategy(IResilienceStrategy strategy)
    {
        throw new NotImplementedException();
    }

    IResiliencePipeline IResiliencePipelineBuilder.Build()
    {
        return Build();
    }
}
