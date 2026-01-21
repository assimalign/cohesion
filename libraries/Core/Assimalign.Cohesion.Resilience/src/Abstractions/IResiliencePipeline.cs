using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Resilience pipeline is used to execute the user-provided callbacks.
/// </summary>
public interface IResiliencePipeline
{
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
}