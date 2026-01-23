using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// 
/// </summary>
/// <param name="context"></param>
/// <param name="state"></param>
/// <returns></returns>
public delegate ValueTask<Outcome> ResilienceStrategy(
    ResilienceCallback callback,
    IResilienceContext context, 
    object? state);
