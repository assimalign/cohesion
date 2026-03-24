using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Configures a hedging strategy.
/// </summary>
public sealed class HedgingStrategyOptions
{
    /// <summary>
    /// Gets or sets the time provider used for hedging delays.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Gets or sets the total number of attempts, including the primary attempt.
    /// </summary>
    public int MaxHedgedAttempts { get; set; } = 2;

    /// <summary>
    /// Gets or sets the delay between hedged attempts.
    /// </summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Gets or sets the predicate that determines whether a failed attempt should allow more hedged attempts to continue.
    /// </summary>
    public Func<HedgingPredicateArguments, ValueTask<bool>> ShouldHandle { get; set; } = static args
        => ValueTask.FromResult(args.Exception is not OperationCanceledException);

    /// <summary>
    /// Gets or sets the callback invoked when an additional hedged attempt is scheduled.
    /// </summary>
    public Func<OnHedgingArguments, ValueTask>? OnHedging { get; set; }
}
