using System;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// A connection-level HTTP/3 error (RFC 9114 §8): a condition — detected on any stream — that the RFC
/// requires to close the whole connection, carrying the RFC 9114 §8.1 / RFC 9204 §6 error code the
/// connection is closed with (for example <c>H3_FRAME_ERROR</c> for a frame truncated by the stream's
/// end, RFC 9114 §7.1, or <c>H3_FRAME_UNEXPECTED</c> for an invalid frame sequence, RFC 9114 §4.1).
/// </summary>
/// <remarks>
/// <para>
/// The first connection error raised on a connection wins: <c>Http3ConnectionContext</c> records it,
/// stops accepting streams, and aborts the multiplexed connection with the exception as the reason. A
/// connection that carries a close code
/// (<see cref="Assimalign.Cohesion.Connections.IMultiplexedConnectionAbort"/>, which the QUIC driver
/// implements) closes with <see cref="ErrorCode"/>; any other closes with its own default, and the code
/// travels only with the reason (see docs/DESIGN.md).
/// </para>
/// <para>
/// Derives from <see cref="IOException"/> so an application reading a request body observes the
/// connection failing exactly as it observes any other body-read failure.
/// </para>
/// </remarks>
internal sealed class Http3ConnectionException : IOException
{
    /// <summary>
    /// Initializes a new connection-level error.
    /// </summary>
    /// <param name="errorCode">The error code the connection is closed with.</param>
    /// <param name="message">A diagnostic description of the error.</param>
    public Http3ConnectionException(Http3ErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a new connection-level error that wraps its cause.
    /// </summary>
    /// <param name="errorCode">The error code the connection is closed with.</param>
    /// <param name="message">A diagnostic description of the error.</param>
    /// <param name="innerException">The failure that caused the error.</param>
    public Http3ConnectionException(Http3ErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the error code the connection is closed with.
    /// </summary>
    public Http3ErrorCode ErrorCode { get; }
}
