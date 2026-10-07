using System;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Client;

/// <summary>
/// Thrown when a client operation fails: a failed dial, a server error frame, a broken
/// connection mid-exchange, or invalid connection settings.
/// </summary>
/// <remarks>
/// Server-reported failures carry the wire's stable <see cref="ProtocolErrorCode"/>
/// in <see cref="Code"/>. A failed dial uses <see cref="ProtocolErrorCode.ConnectionFailure"/>
/// and keeps the transport's exception as <see cref="Exception.InnerException"/>; other
/// client-local failures (settings, a transport broken after the dial, in the handshake or in
/// an exchange) use <see cref="ProtocolErrorCode.Internal"/>, and a broken transport keeps its
/// exception (an <see cref="System.IO.IOException"/>, a
/// <see cref="System.Net.Sockets.SocketException"/>, a connection reset or abort) as the inner
/// exception too. No server sends <see cref="ProtocolErrorCode.ConnectionFailure"/>: an error
/// frame that carries it is a <see cref="ProtocolErrorCode.ProtocolViolation"/>.
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
