namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

internal partial class Http2Frame
{
    public Http2SettingsFrameFlags SettingsFlags
    {
        get => (Http2SettingsFrameFlags)Flags;
        set => Flags = (byte)value;
    }

    public bool SettingsAck => (SettingsFlags & Http2SettingsFrameFlags.Acknowledge) == Http2SettingsFrameFlags.Acknowledge;

    public void PrepareSettings(Http2SettingsFrameFlags flags)
    {
        PayloadLength = 0;
        Type = Http2FrameType.Settings;
        SettingsFlags = flags;
        StreamId = 0;
    }
}
