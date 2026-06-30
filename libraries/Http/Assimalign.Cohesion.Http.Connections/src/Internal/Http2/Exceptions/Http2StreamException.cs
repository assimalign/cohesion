namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// Stream-level HTTP/2 protocol error. Unlike <see cref="Http2ConnectionException"/>
/// (which forces the entire connection to terminate with a <c>GOAWAY</c>),
/// a stream error is recoverable: the offending stream is reset with
/// <c>RST_STREAM</c> carrying <see cref="ErrorCode"/>, and the connection
/// keeps processing other streams (RFC 9113 §5.4.2).
/// </summary>
internal sealed class Http2StreamException : HttpException
{
    public Http2StreamException(int streamId, Http2ErrorCode errorCode, string message)
        : base(message)
    {
        StreamId = streamId;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// The stream the error applies to. Carried in the outbound
    /// <c>RST_STREAM</c> frame.
    /// </summary>
    public int StreamId { get; }

    /// <summary>
    /// The error code carried in the outbound <c>RST_STREAM</c> frame.
    /// </summary>
    public Http2ErrorCode ErrorCode { get; }
}
