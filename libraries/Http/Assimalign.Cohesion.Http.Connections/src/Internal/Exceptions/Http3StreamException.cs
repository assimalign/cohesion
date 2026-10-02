using System;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// A stream-level HTTP/3 condition that ends one request stream, carrying the RFC 9114 §8.1 error code
/// that names it: a stream error (the stream is reset, RFC 9114 §8) or the reason the server stopped
/// reading the request (<c>STOP_SENDING</c>, RFC 9114 §4.1). The QUIC connection and its other streams
/// are unaffected — unlike <see cref="Http3ConnectionException"/>.
/// </summary>
/// <remarks>
/// <para>
/// The transport hands this exception to the connection abstraction as the reason whenever it resets a
/// request stream (<see cref="Assimalign.Cohesion.Connections.IConnection.Abort(Exception)"/>) or stops
/// reading one (<see cref="System.IO.Pipelines.PipeReader.Complete(Exception)"/>), so the intended code
/// travels with the signal. The in-memory driver surfaces the reason to the peer verbatim; the QUIC
/// driver resets with its configured default stream error code, because the connection contract has no
/// per-call application error code (see docs/DESIGN.md, "Request streams").
/// </para>
/// <para>
/// Derives from <see cref="IOException"/> so an application reading the request body observes a stream
/// error exactly as it observes any other body-read failure.
/// </para>
/// </remarks>
internal sealed class Http3StreamException : IOException
{
    /// <summary>
    /// Initializes a new stream-level condition.
    /// </summary>
    /// <param name="errorCode">The RFC 9114 §8.1 error code the stream is reset (or stopped) with.</param>
    /// <param name="message">A diagnostic description of the condition.</param>
    public Http3StreamException(Http3ErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a new stream-level condition that wraps its cause.
    /// </summary>
    /// <param name="errorCode">The RFC 9114 §8.1 error code the stream is reset (or stopped) with.</param>
    /// <param name="message">A diagnostic description of the condition.</param>
    /// <param name="innerException">The failure that caused the condition.</param>
    public Http3StreamException(Http3ErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the RFC 9114 §8.1 error code the request stream is reset (or stopped) with.
    /// </summary>
    public Http3ErrorCode ErrorCode { get; }
}
