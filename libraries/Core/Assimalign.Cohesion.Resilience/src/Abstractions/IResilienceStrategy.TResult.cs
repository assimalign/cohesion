using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

public interface IResilienceStrategy<TResult>
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="callback"></param>
    /// <returns></returns>
    ValueTask<Outcome<TResult>> ExecuteAsync(
        ResilienceCallback<TResult> callback,
        IResilienceContext context,
        object? state);
}