using System;

namespace Assimalign.Cohesion.Http.Connections.Internal;

[Flags]
internal enum Http2SettingsFrameFlags : byte
{
    None = 0x0,
    Acknowledge = 0x1,
}
