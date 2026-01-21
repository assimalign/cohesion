namespace Assimalign.Cohesion.Resilience;

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
    /// <returns></returns>
    IResiliencePipeline Build();
}
