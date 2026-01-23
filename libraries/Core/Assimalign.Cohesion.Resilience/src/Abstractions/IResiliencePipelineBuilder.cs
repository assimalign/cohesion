using System;

namespace Assimalign.Cohesion.Resilience;

public interface IResiliencePipelineBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="strategy"></param>
    /// <returns></returns>
    IResiliencePipelineBuilder UseStrategy(Func<ResilienceStrategy, ResilienceStrategy> strategy);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IResiliencePipeline Build();
}
