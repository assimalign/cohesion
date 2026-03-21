using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents a strategy-specific failure captured by the resilience pipeline.
/// </summary>
public class ResilienceStrategyFailureException : ResilienceException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResilienceStrategyFailureException"/> class.
    /// </summary>
    /// <param name="operationKey">The operation key associated with the failed execution.</param>
    /// <param name="message">The failure message.</param>
    public ResilienceStrategyFailureException(OperationKey operationKey, string message)
        : this(operationKey, message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResilienceStrategyFailureException"/> class.
    /// </summary>
    /// <param name="operationKey">The operation key associated with the failed execution.</param>
    /// <param name="message">The failure message.</param>
    /// <param name="reason">The underlying failure, if one exists.</param>
    public ResilienceStrategyFailureException(
        OperationKey operationKey,
        string message,
        Exception? reason)
        : base(ResilienceErrorCode.StrategyFailure, operationKey, message, reason)
    {
    }
}
