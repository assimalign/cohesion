using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

/* https://tools.ietf.org/html/rfc7540#section-6.5.1
    List of:
    +-------------------------------+
    |       Identifier (16)         |
    +-------------------------------+-------------------------------+
    |                        Value (32)                             |
    +---------------------------------------------------------------+
*/
internal partial class Http2Frame
{
    public Http2SettingsFrameFlags SettingsFlags
    {
        get => (Http2SettingsFrameFlags)Flags;
        set => Flags = (byte)value;
    }

    public bool SettingsAck => (SettingsFlags & Http2SettingsFrameFlags.Acknowledge) == Http2SettingsFrameFlags.Acknowledge;

    public void PrepareSettings(Http2SettingsFrameFlags flags)
    {
        PayloadLength = 0;
        Type = Http2FrameType.Settings;
        SettingsFlags = flags;
        StreamId = 0;
    }
}