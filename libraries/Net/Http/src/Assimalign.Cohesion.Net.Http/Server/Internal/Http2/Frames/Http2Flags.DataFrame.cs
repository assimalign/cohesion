using System;

namespace Assimalign.Cohesion.Net.Http.Internal;

[Flags]
internal enum Http2DataFrameFlags : byte
{
    None = 0x0,
    EndStream = 0x1,
    Padded = 0x8
}
