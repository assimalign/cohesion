namespace Assimalign.Cohesion.Resilience.Retry.Internal;

/// <summary>
/// Marker interface for outcome arguments.
/// </summary>
/// <typeparam name="TResult">The type of result.</typeparam>
internal interface IOutcomeArguments<TResult>
{
    /// <summary>
    /// Gets the resilience context.
    /// </summary>
    ResilienceContextO Context { get; }

    /// <summary>
    /// Gets the outcome.
    /// </summary>
    OutcomeO<TResult> Outcome { get; }
}