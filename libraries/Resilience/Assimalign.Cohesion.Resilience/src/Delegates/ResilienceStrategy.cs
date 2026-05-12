using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents a delegate that executes a resilience strategy around a specified callback operation.
/// </summary>
/// <remarks>
/// The delegate enables the implementation of cross-cutting resilience behaviors, such as retries,
/// circuit breakers, or timeouts, by wrapping the provided callback. The strategy may alter the execution flow or
/// handle exceptions according to its policy.
/// </remarks>
/// <param name="callback">The operation to execute within the resilience strategy. This callback encapsulates the core logic to be protected by the strategy.</param>
/// <param name="context">The context that provides information and state relevant to the execution of the resilience strategy. Cannot be null.</param>
/// <param name="state">An optional user-defined state object that is passed to the callback. May be null.</param>
/// <returns>A ValueTask that represents the asynchronous execution of the resilience strategy. The result contains the outcome of the operation, including success or failure information.</returns>
public delegate ValueTask<Outcome> ResilienceStrategy(
    ResilienceCallback callback,
    IResilienceContext context, 
    object? state);
