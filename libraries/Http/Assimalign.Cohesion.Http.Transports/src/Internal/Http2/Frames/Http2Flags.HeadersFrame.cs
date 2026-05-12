using System;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

[Flags]
internal enum Http2HeadersFrameFlags : byte
{
    None = 0x0,
    EndStream = 0x1,
    EndHeaders = 0x4,
    Padded = 0x8,
    Priority = 0x20,
}
