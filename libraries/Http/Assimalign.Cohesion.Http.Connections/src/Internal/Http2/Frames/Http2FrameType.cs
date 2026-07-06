namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

internal enum Http2FrameType : byte
{
    Data = 0x0,
    Headers = 0x1,
    // RFC 9113 §5.3.2 / RFC 7540 §6.3 — the deprecated stream-priority frame.
    // The server advertises SETTINGS_NO_RFC7540_PRIORITIES = 1 and ignores this
    // frame; RFC 9218 PRIORITY_UPDATE (0x10) is the replacement mechanism.
    Priority = 0x2,
    RstStream = 0x3,
    Settings = 0x4,
    PushPromise = 0x5,
    Ping = 0x6,
    GoAway = 0x7,
    WindowUpdate = 0x8,
    Continuation = 0x9,
    // RFC 9218 §7.1 — the HTTP/2 PRIORITY_UPDATE frame carries a request
    // stream's re-prioritization on stream 0.
    PriorityUpdate = 0x10,
}
