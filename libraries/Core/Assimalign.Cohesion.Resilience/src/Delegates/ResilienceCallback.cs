using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents an asynchronous callback that executes resilience logic within a given context and optional state.
/// </summary>
/// <remarks>
/// Use this delegate to define custom logic that should be executed as part of a resilience strategy,
/// such as retries, circuit breakers, or fallback operations. The callback receives the current execution context and
/// an optional state object, allowing for flexible and reusable resilience policies.
/// </remarks>
/// <param name="context">The execution context that provides information and services for the resilience operation. Cannot be null.</param>
/// <param name="state">An optional user-defined state object to be passed to the callback. May be null if no state is required.</param>
/// <returns>A ValueTask that represents the asynchronous operation of the resilience callback.</returns>
public delegate ValueTask ResilienceCallback(IResilienceContext context, object? state);