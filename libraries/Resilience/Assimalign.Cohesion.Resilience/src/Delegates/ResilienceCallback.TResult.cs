using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents an asynchronous callback that executes resilient operations and returns a result of the specified type.
/// </summary>
/// <remarks>This delegate is typically used to encapsulate user-defined logic that should be executed within a
/// resilience strategy, such as retries or circuit breakers. The callback receives an execution context and an optional
/// state object, allowing for flexible and reusable operation definitions.</remarks>
/// <typeparam name="TResult">The type of the result returned by the callback.</typeparam>
/// <param name="context">The context for the resilience operation, providing execution and policy information. Cannot be null.</param>
/// <param name="state">An optional state object to pass contextual information to the callback. May be null.</param>
/// <returns>A ValueTask that represents the asynchronous operation and yields the result of type TResult when completed.</returns>
public delegate ValueTask<TResult> ResilienceCallback<TResult>(IResilienceContext context, object? state);
