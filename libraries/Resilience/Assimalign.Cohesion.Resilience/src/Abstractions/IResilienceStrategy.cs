using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

public interface IResilienceStrategy
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="callback"></param>
    /// <returns></returns>
    ValueTask<Outcome> ExecuteAsync(
        ResilienceCallback callback,
        IResilienceContext context,
        object? state);
}