using System;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// Thrown when a client operation fails: a server error frame, a broken
/// connection mid-exchange, or invalid connection settings.
/// </summary>
/// <remarks>
/// Server-reported failures carry the wire's stable <see cref="ProtocolErrorCode"/>
/// in <see cref="Code"/>; client-local failures (settings, broken transport) use
/// <see cref="ProtocolErrorCode.Internal"/>.
/// </remarks>
public class DatabaseClientException : DatabaseException
{
    /// <summary>
    /// Initializes a new <see cref="DatabaseClientException"/>.
    /// </summary>
    /// <param name="code">The wire error code the failure maps to.</param>
    /// <param name="message">The error message.</param>
    public DatabaseClientException(ProtocolErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>
    /// Initializes a new <see cref="DatabaseClientException"/> with an inner exception.
    /// </summary>
    /// <param name="code">The wire error code the failure maps to.</param>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying cause.</param>
    public DatabaseClientException(ProtocolErrorCode code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>
    /// Gets the wire error code of the failure.
    /// </summary>
    public ProtocolErrorCode Code { get; }
}
