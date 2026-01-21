using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Resilience;

public interface IResiliencePipelineBuilder<TResult>
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="strategy"></param>
    /// <returns></returns>
    IResiliencePipelineBuilder<TResult> UseStrategy(IResilienceStrategy<TResult> strategy);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IResiliencePipeline<TResult> Build();
}
