using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Configures fallback behavior for callbacks that do not return a result.
/// </summary>
public sealed class FallbackStrategyOptions
{
    /// <summary>
    /// Gets or sets the predicate that determines whether the fallback should handle a failed outcome.
    /// </summary>
    public Func<FallbackPredicateArguments, ValueTask<bool>> ShouldHandle { get; set; } = static args
        => ValueTask.FromResult(
            args.Outcome.IsFailure(out Exception? exception) &&
            exception is not OperationCanceledException);

    /// <summary>
    /// Gets or sets the fallback action that runs after a handled failure.
    /// </summary>
    public Func<FallbackActionArguments, ValueTask>? FallbackAction { get; set; }
}
