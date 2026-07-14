using System;

namespace Assimalign.Cohesion.Database.Protocol;

/// <summary>
/// Thrown when incoming bytes violate the wire protocol: bad framing, an
/// oversized length prefix, or an out-of-order message.
/// </summary>
/// <remarks>
/// The protocol package is a child root of the Database area and owns an
/// independent exception root (area exception-scoping rule): it does not derive
/// from <c>DatabaseException</c>, so consumers of the protocol alone never load
/// the area contracts, and layers that own both vocabularies (the server session
/// pump, the client core) map protocol violations explicitly.
/// </remarks>
public class ProtocolException : Exception
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
