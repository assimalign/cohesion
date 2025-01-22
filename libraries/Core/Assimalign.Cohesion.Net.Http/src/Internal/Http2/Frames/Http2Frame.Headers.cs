namespace Assimalign.Cohesion.Net.Http.Internal;

/* https://tools.ietf.org/html/rfc7540#section-6.2
    +---------------+
    |Pad Length? (8)|
    +-+-------------+-----------------------------------------------+
    |E|                 Stream Dependency? (31)                     |
    +-+-------------+-----------------------------------------------+
    |  Weight? (8)  |
    +-+-------------+-----------------------------------------------+
    |                   Header Block Fragment (*)                 ...
    +---------------------------------------------------------------+
    |                           Padding (*)                       ...
    +---------------------------------------------------------------+
*/
internal partial class Http2Frame
{
    public Http2HeadersFrameFlags HeadersFlags
    {
        get => (Http2HeadersFrameFlags)Flags;
        set => Flags = (byte)value;
    }

    public bool HeadersEndHeaders => (HeadersFlags & Http2HeadersFrameFlags.EndHeaders) == Http2HeadersFrameFlags.EndHeaders;
    public bool HeadersEndStream => (HeadersFlags & Http2HeadersFrameFlags.EndStrem) == Http2HeadersFrameFlags.EndStrem;
    public bool HeadersHasPadding => (HeadersFlags & Http2HeadersFrameFlags.Padded) == Http2HeadersFrameFlags.Padded;
    public bool HeadersHasPriority => (HeadersFlags & Http2HeadersFrameFlags.Priority) == Http2HeadersFrameFlags.Priority;
    public byte HeadersPadLength { get; set; }
    public int HeadersStreamDependency { get; set; }
    public byte HeadersPriorityWeight { get; set; }
    private int HeadersPayloadOffset => (HeadersHasPadding ? 1 : 0) + (HeadersHasPriority ? 5 : 0);
    public int HeadersPayloadLength => PayloadLength - HeadersPayloadOffset - HeadersPadLength;
    public void PrepareHeaders(Http2HeadersFrameFlags flags, int streamId)
    {
        PayloadLength = 0;
        Type = Http2FrameType.Headers;
        HeadersFlags = flags;
        StreamId = streamId;
    }
}