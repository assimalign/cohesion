namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

internal partial class Http2Frame
{
    public int GoAwayLastStreamId { get; set; }

    public Http2ErrorCode GoAwayErrorCode { get; set; }

    public void PrepareGoAway(int lastStreamId, Http2ErrorCode errorCode)
    {
        PayloadLength = 8;
        Type = Http2FrameType.GoAway;
        Flags = 0;
        StreamId = 0;
        GoAwayLastStreamId = lastStreamId;
        GoAwayErrorCode = errorCode;
    }
}
