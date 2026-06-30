using System;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// Serves as the base exception for connection-related errors in the Cohesion networking stack.
/// </summary>
public class ConnectionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionException"/> class.
    /// </summary>
    public ConnectionException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ConnectionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
