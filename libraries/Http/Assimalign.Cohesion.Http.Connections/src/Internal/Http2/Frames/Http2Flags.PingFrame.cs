using System;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

[Flags]
internal enum Http2PingFrameFlags : byte
{
    None = 0x0,
    Acknowledge = 0x1,
}
