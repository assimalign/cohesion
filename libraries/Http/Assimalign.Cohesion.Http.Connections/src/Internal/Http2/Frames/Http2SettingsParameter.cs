namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

internal enum Http2SettingsParameter : ushort
{
    SETTINGS_HEADER_TABLE_SIZE = 0x1,
    SETTINGS_ENABLE_PUSH = 0x2,
    SETTINGS_MAX_CONCURRENT_STREAMS = 0x3,
    SETTINGS_INITIAL_WINDOW_SIZE = 0x4,
    SETTINGS_MAX_FRAME_SIZE = 0x5,
    SETTINGS_MAX_HEADER_LIST_SIZE = 0x6,
    SETTINGS_ENABLE_CONNECT_PROTOCOL = 0x8,
    // RFC 9218 §2.1 — advertised as 1 to tell the peer the server does not use
    // the deprecated RFC 7540 stream-priority scheme and instead applies the
    // extensible-priorities scheme (Priority header + PRIORITY_UPDATE).
    SETTINGS_NO_RFC7540_PRIORITIES = 0x9,
}
