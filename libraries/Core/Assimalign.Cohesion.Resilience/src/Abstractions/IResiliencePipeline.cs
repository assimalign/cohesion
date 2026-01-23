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
    /// <param name="callback"></param>
    /// <param name="context"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    ValueTask ExecuteAsync(
        ResilienceCallback callback,
        IResilienceContext context,
        object? state);
}