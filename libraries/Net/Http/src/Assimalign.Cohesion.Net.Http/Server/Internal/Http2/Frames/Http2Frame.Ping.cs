namespace Assimalign.Cohesion.Net.Http.Internal;

/* https://tools.ietf.org/html/rfc7540#section-6.7
    +---------------------------------------------------------------+
    |                                                               |
    |                      Opaque Data (64)                         |
    |                                                               |
    +---------------------------------------------------------------+
*/
internal partial class Http2Frame
{
    public Http2PingFrameFlags PingFlags
    {
        get => (Http2PingFrameFlags)Flags;
        set => Flags = (byte)value;
    }
    public bool PingAck => (PingFlags & Http2PingFrameFlags.Acknowledge) == Http2PingFrameFlags.Acknowledge;
    public void PreparePing(Http2PingFrameFlags flags)
    {
        PayloadLength = 8;
        Type = Http2FrameType.Ping;
        PingFlags = flags;
        StreamId = 0;
    }
}