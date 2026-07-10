using System;

using Assimalign.Cohesion.Http.Connections.Internal.Http3.Frames;
using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http3.QPack;

/// <summary>
/// A QPACK connection-level error (RFC 9204 §2.2 / §8.3). Unlike a per-stream
/// field-section parse failure — which drops the offending request stream and
/// lets the connection survive — a <see cref="QPackException"/> is fatal to the
/// whole HTTP/3 connection: the dynamic table state is shared across streams, so
/// a corrupt encoder instruction, an unsatisfiable Required Insert Count, or a
/// blocked-stream-limit violation cannot be isolated to one stream.
/// </summary>
/// <remarks>
/// The carried <see cref="ErrorCode"/> is the QPACK error the peer would be told
/// on connection close: <c>QPACK_DECOMPRESSION_FAILED</c>,
/// <c>QPACK_ENCODER_STREAM_ERROR</c>, or <c>QPACK_DECODER_STREAM_ERROR</c>.
/// </remarks>
internal sealed class QPackException : Http3Exception
{
    /// <summary>
    /// Initializes a new <see cref="QPackException"/>.
    /// </summary>
    /// <param name="errorCode">The QPACK connection error code to report.</param>
    /// <param name="message">A description of the failure.</param>
    public QPackException(Http3ErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Initializes a new <see cref="QPackException"/> that wraps an underlying cause.
    /// </summary>
    /// <param name="errorCode">The QPACK connection error code to report.</param>
    /// <param name="message">A description of the failure.</param>
    /// <param name="inner">The underlying exception that triggered this failure.</param>
    public QPackException(Http3ErrorCode errorCode, string message, Exception inner)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the QPACK connection error code associated with this failure.
    /// </summary>
    public Http3ErrorCode ErrorCode { get; }
}
