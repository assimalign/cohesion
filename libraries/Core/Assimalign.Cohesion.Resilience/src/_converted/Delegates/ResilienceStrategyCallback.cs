using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// 
/// </summary>
/// <typeparam name="TState"></typeparam>
/// <param name="context"></param>
/// <param name="state"></param>
/// <returns></returns>
public delegate ValueTask<Outcome> ResilienceStrategyCallback<TState>(
    IResilienceContext context, 
    TState state);
