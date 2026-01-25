using System;

namespace Assimalign.Cohesion.Resilience;

public interface IResiliencePipelineBuilder<TResult>
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="strategy"></param>
    /// <returns></returns>
    IResiliencePipelineBuilder<TResult> UseStrategy(Func<ResilienceStrategy<TResult>, ResilienceStrategy<TResult>> strategy);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IResiliencePipeline<TResult> Build();
}
