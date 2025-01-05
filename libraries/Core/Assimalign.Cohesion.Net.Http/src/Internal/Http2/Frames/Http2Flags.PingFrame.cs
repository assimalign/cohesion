using System;

namespace Assimalign.Cohesion.Net.Http.Internal;

[Flags]
internal enum Http2PingFrameFlags : byte
{
    None = 0x0,
    Acknowledge = 0x1
}