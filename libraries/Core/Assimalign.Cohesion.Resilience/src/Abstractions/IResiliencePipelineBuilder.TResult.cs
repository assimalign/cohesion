using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// 
/// </summary>
/// <typeparam name="TResult"></typeparam>
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
    /// <param name="strategy"></param>
    /// <returns></returns>
    IResiliencePipelineBuilder<TResult> UseStrategy(ResilienceStrategy<TResult> strategy);

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
