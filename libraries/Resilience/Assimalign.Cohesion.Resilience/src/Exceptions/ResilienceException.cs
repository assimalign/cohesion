using System;

namespace Assimalign.Cohesion.Resilience;

using Properties;

/// <summary>
/// Represents a resilience-specific failure that occurred while executing a pipeline or strategy.
/// </summary>
public class ResilienceException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResilienceException"/> class.
    /// </summary>
    /// <param name="code">The resilience error code associated with the failure.</param>
    /// <param name="operationKey">The operation key associated with the execution.</param>
    /// <param name="message">The failure message.</param>
    public ResilienceException(ResilienceErrorCode code, OperationKey operationKey, string message)
        : this(code, operationKey, message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResilienceException"/> class.
    /// </summary>
    /// <param name="code">The resilience error code associated with the failure.</param>
    /// <param name="operationKey">The operation key associated with the execution.</param>
    /// <param name="message">The failure message.</param>
    /// <param name="innerException">The underlying failure, if one exists.</param>
    public ResilienceException(
        ResilienceErrorCode code,
        OperationKey operationKey,
        string message,
        Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
        OperationKey = operationKey;
    }

    /// <summary>
    /// Gets the operation key associated with the failed execution.
    /// </summary>
    public OperationKey OperationKey { get; }

    /// <summary>
    /// Gets the resilience error code associated with the failure.
    /// </summary>
    public ResilienceErrorCode Code { get; }

    /// <summary>
    /// Creates a pipeline failure exception for the specified operation.
    /// </summary>
    /// <param name="operationKey">The operation key associated with the execution.</param>
    /// <param name="innerException">The underlying failure, if one exists.</param>
    /// <returns>A new <see cref="ResilienceException"/> describing the pipeline failure.</returns>
    public static ResilienceException PipelineFailure(
        OperationKey operationKey = default,
        Exception? innerException = null)
    {
        return new ResilienceException(
            ResilienceErrorCode.PipelineFailure,
            operationKey,
            ErrorMessages.PipelineFailure,
            innerException);
    }

    /// <summary>
    /// Creates a strategy failure exception for the specified operation.
    /// </summary>
    /// <param name="operationKey">The operation key associated with the execution.</param>
    /// <param name="innerException">The underlying failure, if one exists.</param>
    /// <returns>A new <see cref="ResilienceException"/> describing the strategy failure.</returns>
    public static ResilienceException StrategyFailure(
        OperationKey operationKey = default,
        Exception? innerException = null)
    {
        return new ResilienceException(
            ResilienceErrorCode.StrategyFailure,
            operationKey,
            "The resilience strategy completed without a successful outcome.",
            innerException);
    }
}
