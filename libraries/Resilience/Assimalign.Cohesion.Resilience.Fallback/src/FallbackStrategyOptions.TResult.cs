using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Configures fallback behavior for callbacks that return a result.
/// </summary>
/// <typeparam name="TResult">The callback result type.</typeparam>
public sealed class FallbackStrategyOptions<TResult>
{
    /// <summary>
    /// Gets or sets the predicate that determines whether the fallback should handle a failed outcome.
    /// </summary>
    public Func<FallbackPredicateArguments<TResult>, ValueTask<bool>> ShouldHandle { get; set; } = static args
        => ValueTask.FromResult(
            args.Outcome.IsFailure(out Exception? exception) &&
            exception is not OperationCanceledException);

    /// <summary>
    /// Gets or sets the action that produces the fallback result for a handled failure.
    /// </summary>
    public Func<FallbackActionArguments<TResult>, ValueTask<TResult>> FallbackAction { get; set; } = static _
        => ValueTask.FromException<TResult>(new InvalidOperationException("A fallback action must be configured."));
}
