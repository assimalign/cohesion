using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// 
/// </summary>
/// <typeparam name="TResult"></typeparam>
/// <typeparam name="TState"></typeparam>
/// <param name="context"></param>
/// <param name="state"></param>
/// <returns></returns>
public delegate ValueTask<Outcome<TResult>> ResilienceStrategyCallback<TResult, TState>(
    IResilienceContext context, 
    TState state);