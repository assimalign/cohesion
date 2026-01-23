using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// 
/// </summary>
/// <typeparam name="TResult"></typeparam>
/// <param name="context"></param>
/// <param name="state"></param>
/// <returns></returns>
public delegate ValueTask<Outcome<TResult>> ResilienceStrategy<TResult>(
    ResilienceCallback<TResult> callback,
    IResilienceContext context, 
    object? state);