using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class UdtUnit
{
    public UdtPacket m_Packet = new UdtPacket();       // packet
    public int m_iFlag;            // 0: free, 1: occupied, 2: msg read but not freed (out-of-order), 3: msg dropped
};