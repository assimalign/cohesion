using System;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

[Flags]
internal enum Http2SettingsFrameFlags : byte
{
    None = 0x0,
    Acknowledge = 0x1,
}
