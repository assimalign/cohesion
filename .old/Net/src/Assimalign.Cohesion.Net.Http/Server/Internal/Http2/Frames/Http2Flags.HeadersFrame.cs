using System;

namespace Assimalign.Cohesion.Net.Http.Internal;

[Flags]
internal enum Http2HeadersFrameFlags : byte
{
    None = 0x0,
    EndStrem = 0x1,
    EndHeaders = 0x4,
    Padded = 0x8,
    Priority = 0x20
}