namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

internal partial class Http2Frame
{
    public int PayloadLength { get; set; }

    public Http2FrameType Type { get; set; }

    public byte Flags { get; set; }

    public int StreamId { get; set; }

    private object ShowFlags()
    {
        return Type switch
        {
            Http2FrameType.Data => DataFlags,
            Http2FrameType.Headers => HeadersFlags,
            Http2FrameType.Settings => SettingsFlags,
            Http2FrameType.Ping => PingFlags,
            _ => $"0x{Flags:x}",
        };
    }

    public override string ToString()
    {
        return $"{Type} Stream: {StreamId} Length: {PayloadLength} Flags: {ShowFlags()}";
    }
}
