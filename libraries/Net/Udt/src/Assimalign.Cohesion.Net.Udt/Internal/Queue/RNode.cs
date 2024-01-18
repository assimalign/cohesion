using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class RNode
{
    public UdtCongestionControl m_pUDT;                // Pointer to the instance of CUDT socket
    public ulong m_llTimeStamp;      // Time Stamp

    public bool m_bOnList;              // if the node is already on the list
};
