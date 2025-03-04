using System;

namespace Assimalign.Cohesion.Web.Http.Internal;

[Flags]
internal enum Http2SettingsFrameFlags : byte
{
    None = 0x0,
    Acknowledge = 0x1,
}
