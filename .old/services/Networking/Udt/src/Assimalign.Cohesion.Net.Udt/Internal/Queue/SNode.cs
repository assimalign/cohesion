using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class SNode
{
    public UdtCongestionControl m_pUDT;       // Pointer to the instance of CUDT socket
    public ulong m_llTimeStamp;      // Time Stamp

    public int m_iHeapLoc;     // location on the heap, -1 means not on the heap
}
