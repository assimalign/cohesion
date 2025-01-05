﻿namespace Assimalign.Cohesion.Net.Http.Internal;

/* https://tools.ietf.org/html/rfc7540#section-6.8
    +-+-------------------------------------------------------------+
    |R|                  Last-Stream-ID (31)                        |
    +-+-------------------------------------------------------------+
    |                      Error Code (32)                          |
    +---------------------------------------------------------------+
    |                  Additional Debug Data (*)                    |
    +---------------------------------------------------------------+
*/
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