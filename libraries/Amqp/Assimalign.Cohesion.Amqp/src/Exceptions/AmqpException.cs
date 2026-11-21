using System;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// The exception that reports a failure in the AMQP library family: a protocol violation, a closed
/// connection, an ended session, a detached link, or a failed authentication.
/// </summary>
/// <remarks>
/// Branch on <see cref="ErrorCode"/> rather than on the message. When the failure carries an AMQP error,
/// such as the error a peer attached to its <c>close</c>, <c>end</c> or <c>detach</c>, it is in
/// <see cref="Error"/>.
/// </remarks>
public class AmqpException : Exception
{
    /// <summary>
    /// Initializes a new AMQP exception with <see cref="AmqpErrorCode.Other"/>.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public AmqpException(string message)
        : this(AmqpErrorCode.Other, message, innerException: null)
    {
    }

    /// <summary>
    /// Initializes a new AMQP exception with <see cref="AmqpErrorCode.Other"/>.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The exception that caused this one, or <see langword="null"/>.</param>
    public AmqpException(string message, Exception? innerException)
        : this(AmqpErrorCode.Other, message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new AMQP exception.
    /// </summary>
    /// <param name="errorCode">The failure category.</param>
    /// <param name="message">The exception message.</param>
    public AmqpException(AmqpErrorCode errorCode, string message)
        : this(errorCode, message, innerException: null)
    {
    }

    /// <summary>
    /// Initializes a new AMQP exception.
    /// </summary>
    /// <param name="errorCode">The failure category.</param>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The exception that caused this one, or <see langword="null"/>.</param>
    public AmqpException(AmqpErrorCode errorCode, string message, Exception? innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the failure category.
    /// </summary>
    public AmqpErrorCode ErrorCode { get; }

    /// <summary>
    /// Gets the AMQP error behind the failure, or <see langword="null"/> when there is none, such as when
    /// a carrier ended without a <c>close</c>.
    /// </summary>
    public AmqpError? Error { get; init; }
}
