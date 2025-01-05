using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Internal;

/* https://tools.ietf.org/html/rfc7540#section-4.1
    +-----------------------------------------------+
    |                 Length (24)                   |
    +---------------+---------------+---------------+
    |   Type (8)    |   Flags (8)   |
    +-+-------------+---------------+-------------------------------+
    |R|                 Stream Identifier (31)                      |
    +=+=============================================================+
    |                   Frame Payload (0...)                      ...
    +---------------------------------------------------------------+
*/
internal partial class Http2Frame
{
    public int PayloadLength { get; set; }
    public Http2FrameType Type { get; set; }
    public byte Flags { get; set; }
    public int StreamId { get; set; }

    internal object ShowFlags()
    {
        switch (Type)
        {
            case Http2FrameType.Continuation:
               // return ContinuationFlags;
            case Http2FrameType.Data:
                return DataFlags;
            case Http2FrameType.Headers:
                return HeadersFlags;
            case Http2FrameType.Settings:
                return SettingsFlags;
            case Http2FrameType.Ping:
                return PingFlags;

            // Not Implemented
            case Http2FrameType.PushPromise:

            // No flags defined
            case Http2FrameType.Priority:
            case Http2FrameType.RstStream:
            case Http2FrameType.GoAway:
            case Http2FrameType.WindowUpdate:
            default:
                return $"0x{Flags:x}";
        }
    }

    public override string ToString()
    {
        return $"{Type} Stream: {StreamId} Length: {PayloadLength} Flags: {ShowFlags()}";
    }
}
