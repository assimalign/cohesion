using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

public interface IResilienceStrategy<TResult>
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TState"></typeparam>
    /// <param name="callback"></param>
    /// <returns></returns>
    ValueTask<TResult> ExecuteAsync<TState>(
        ResilienceStrategyCallback<TResult, TState> callback,
        IResilienceContext context,
        TState state);
}
