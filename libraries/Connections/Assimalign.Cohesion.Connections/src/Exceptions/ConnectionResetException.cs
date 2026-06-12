using System;

namespace Assimalign.Cohesion.Connections;

/// <summary>
/// The exception thrown when a connection is reset by the remote peer.
/// </summary>
public sealed class ConnectionResetException : ConnectionException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionResetException"/> class.
    /// </summary>
    public ConnectionResetException()
        : base("The connection was reset by the remote peer.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionResetException"/> class with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ConnectionResetException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionResetException"/> class with a specified error message
    /// and a reference to the inner exception that is the cause of this exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public ConnectionResetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
