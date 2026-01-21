using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

public interface IResilienceStrategy
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TState"></typeparam>
    /// <param name="callback"></param>
    /// <returns></returns>
    ValueTask ExecuteAsync<TState>(
        ResilienceStrategyCallback<TState> callback,
        IResilienceContext context,
        TState state);
}