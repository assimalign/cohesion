using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class RcvUList
{
    public List<RNode> m_nodeList = new List<RNode>();

    public void insert(UdtCongestionControl u)
    {
        RNode n = u.m_pRNode;
        n.m_llTimeStamp = UdtTimer.rdtsc();

        // always insert at the end for RcvUList
        m_nodeList.Add(n);
    }

    public void remove(UdtCongestionControl u)
    {
        RNode n = u.m_pRNode;

        if (!n.m_bOnList)
            return;

        m_nodeList.Remove(n);
    }

    public void update(UdtCongestionControl u)
    {
        RNode n = u.m_pRNode;

        if (!n.m_bOnList)
            return;

        RNode match = m_nodeList.Find(x => x.Equals(n));
        if (match.Equals(default(RNode)))
            return;

        match.m_llTimeStamp = UdtTimer.rdtsc();
    }
}

