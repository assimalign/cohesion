namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

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
