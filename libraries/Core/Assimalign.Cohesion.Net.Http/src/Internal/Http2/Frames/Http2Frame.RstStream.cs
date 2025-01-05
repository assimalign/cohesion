namespace Assimalign.Cohesion.Net.Http.Internal;

/* https://tools.ietf.org/html/rfc7540#section-6.4
    +---------------------------------------------------------------+
    |                        Error Code (32)                        |
    +---------------------------------------------------------------+
*/
internal partial class Http2Frame
{
    public Http2ErrorCode RstStreamErrorCode { get; set; }
    public void PrepareRstStream(int streamId, Http2ErrorCode errorCode)
    {
        PayloadLength = 4;
        Type = Http2FrameType.RstStream;
        Flags = 0;
        StreamId = streamId;
        RstStreamErrorCode = errorCode;
    }
}