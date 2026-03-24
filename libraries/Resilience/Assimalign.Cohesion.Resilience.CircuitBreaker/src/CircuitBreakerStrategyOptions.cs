using System;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Configures a circuit breaker strategy.
/// </summary>
public sealed class CircuitBreakerStrategyOptions
{
    /// <summary>
    /// Gets or sets the time provider used to measure the break duration.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>
    /// Gets or sets the number of handled failures required to open the circuit.
    /// </summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>
    /// Gets or sets the length of time the circuit should remain open.
    /// </summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the predicate that determines whether a failure should count toward opening the circuit.
    /// </summary>
    public Func<CircuitBreakerPredicateArguments, ValueTask<bool>> ShouldHandle { get; set; } = static args
        => ValueTask.FromResult(args.Exception is not OperationCanceledException);

    /// <summary>
    /// Gets or sets the callback invoked when the circuit opens.
    /// </summary>
    public Func<OnCircuitOpenedArguments, ValueTask>? OnOpened { get; set; }

    /// <summary>
    /// Gets or sets the callback invoked when the circuit transitions to half-open.
    /// </summary>
    public Func<OnCircuitHalfOpenedArguments, ValueTask>? OnHalfOpened { get; set; }

    /// <summary>
    /// Gets or sets the callback invoked when the circuit closes.
    /// </summary>
    public Func<OnCircuitClosedArguments, ValueTask>? OnClosed { get; set; }
}
