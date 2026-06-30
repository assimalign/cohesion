using System;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// The exception that is thrown when an AMQP protocol header, frame, or encoded value is invalid.
/// </summary>
public sealed class AmqpProtocolException : Exception
{
    /// <summary>
    /// Initializes a new AMQP protocol exception.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public AmqpProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new AMQP protocol exception.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The exception that caused the current exception.</param>
    public AmqpProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
