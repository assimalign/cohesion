using System;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Thrown when incoming bytes violate the wire protocol: bad framing, an
/// oversized length prefix, or an out-of-order message.
/// </summary>
public class ProtocolException : DatabaseException
{
    /// <summary>
    /// Initializes a new <see cref="ProtocolException"/>.
    /// </summary>
    /// <param name="message">The error message.</param>
    public ProtocolException(string message)
        : base(message) { }

    /// <summary>
    /// Initializes a new <see cref="ProtocolException"/> with an inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public ProtocolException(string message, Exception? innerException)
        : base(message, innerException) { }
}
