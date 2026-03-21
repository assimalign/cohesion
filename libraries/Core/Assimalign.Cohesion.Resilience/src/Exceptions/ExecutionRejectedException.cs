using System;

namespace Assimalign.Cohesion.Resilience;

/// <summary>
/// Represents a resilience failure where execution was rejected before the callback completed.
/// </summary>
public class ExecutionRejectedException : ResilienceException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionRejectedException"/> class.
    /// </summary>
    public ExecutionRejectedException()
        : this(default, "The callback execution was rejected.", null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionRejectedException"/> class.
    /// </summary>
    /// <param name="message">The failure message.</param>
    public ExecutionRejectedException(string message)
        : this(default, message, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionRejectedException"/> class.
    /// </summary>
    /// <param name="message">The failure message.</param>
    /// <param name="innerException">The underlying failure, if one exists.</param>
    public ExecutionRejectedException(string message, Exception? innerException)
        : this(default, message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecutionRejectedException"/> class.
    /// </summary>
    /// <param name="operationKey">The operation key associated with the rejected execution.</param>
    /// <param name="message">The failure message.</param>
    /// <param name="innerException">The underlying failure, if one exists.</param>
    public ExecutionRejectedException(
        OperationKey operationKey,
        string message,
        Exception? innerException = null)
        : base(ResilienceErrorCode.ExecutionRejected, operationKey, message, innerException)
    {
    }
}
