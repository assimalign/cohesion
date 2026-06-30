namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// Connection-level HTTP/2 protocol error. The error code travels back
/// to the peer in a <c>GOAWAY</c> frame before the underlying transport
/// is torn down (RFC 9113 §6.8).
/// </summary>
internal sealed class Http2ConnectionException : HttpException
{
    public Http2ConnectionException(Http2ErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// The error code carried in the outbound <c>GOAWAY</c> frame.
    /// </summary>
    public Http2ErrorCode ErrorCode { get; }
}
