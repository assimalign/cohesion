using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class UdtMultiplexer
{
    internal SndQueue m_pSndQueue; // The sending queue
    internal RcvQueue m_pRcvQueue; // The receiving queue
    internal UdtChannel m_pChannel;   // The UDP channel for sending and receiving
    internal UdtTimer m_pTimer;       // The timer

    internal int m_iPort;            // The UDP port number of this multiplexer
    internal AddressFamily m_iIPversion;       // IP version
    internal int m_iMSS;         // Maximum Segment Size
    internal int m_iRefCount;        // number of UDT instances that are associated with this multiplexer
    internal bool m_bReusable;       // if this one can be shared with others
    internal int m_iID;          // multiplexer ID
}
