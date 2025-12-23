using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// 
/// </summary>
public interface IResiliencePipeline
{
    /// <summary>
    /// The strategy to use in the pipeline.
    /// </summary>
    IResilienceStrategy Strategy { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TState"></typeparam>
    /// <param name="callback"></param>
    /// <param name="context"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    ValueTask ExecuteAsync<TState>(
        ResiliencePipelineCallback<TState> callback,
        IResilienceContext context,
        TState state);

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TResult"></typeparam>
    /// <typeparam name="TState"></typeparam>
    /// <param name="callback"></param>
    /// <param name="context"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    ValueTask<TResult> ExecuteAsync<TResult, TState>(
        ResiliencePipelineCallback<TResult, TState> callback,
        IResilienceContext context,
        TState state);
}