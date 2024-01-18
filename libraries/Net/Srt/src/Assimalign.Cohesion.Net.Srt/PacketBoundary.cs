using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Srt;

/// To define packets in order in the buffer. This is public due to being used in buffer.
public enum PacketBoundary
{
    PB_SUBSEQUENT = 0, // 00
    ///      01: last packet of a message
    PB_LAST = 1, // 01
    ///      10: first packet of a message
    PB_FIRST = 2, // 10
    ///      11: solo message packet
    PB_SOLO = 3, // 11
}
