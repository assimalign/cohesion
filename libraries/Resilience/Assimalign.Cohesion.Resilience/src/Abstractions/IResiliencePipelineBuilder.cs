using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// 
/// </summary>
public interface IResiliencePipelineBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="strategy"></param>
    /// <returns></returns>
    IResiliencePipelineBuilder UseStrategy(IResilienceStrategy strategy);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="strategy"></param>
    /// <returns></returns>
    IResiliencePipelineBuilder UseStrategy(ResilienceStrategy strategy);

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

