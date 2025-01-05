using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class SndUList
{
    object m_ListLock = new object();

    public object m_pWindowLock;
    public EventWaitHandle m_pWindowCond;

    SNode[] m_pHeap;           // The heap array
    int m_iArrayLength;         // physical length of the array
    int m_iLastEntry;           // position of last entry on the heap array

    public UdtTimer m_pTimer;

    public SndUList()
    {
        m_iArrayLength = 4096;
        m_iLastEntry = -1;

        m_pHeap = new SNode[m_iArrayLength];
    }

    public void insert(ulong ts, UdtCongestionControl u)
    {
        lock (m_ListLock)
        {
            // increase the heap array size if necessary
            if (m_iLastEntry == m_iArrayLength - 1)
            {
                Array.Resize(ref m_pHeap, m_iArrayLength * 2);
                m_iArrayLength *= 2;
            }

            insert_(ts, u);
        }
    }

    public void update(UdtCongestionControl u, bool reschedule = true)
    {
        lock (m_ListLock)
        {
            SNode n = u.m_pSNode;

            if (n.m_iHeapLoc >= 0)
            {
                if (!reschedule)
                    return;

                if (n.m_iHeapLoc == 0)
                {
                    n.m_llTimeStamp = 1;
                    m_pTimer.interrupt();
                    return;
                }

                remove_(u);
            }

            insert_(1, u);
        }
    }

    public int pop(ref IPEndPoint addr, ref UdtPacket pkt)
    {
        lock (m_ListLock)
        {
            if (-1 == m_iLastEntry)
                return -1;

            // no pop until the next schedulled time
            ulong ts = UdtTimer.rdtsc();
            if (ts < m_pHeap[0].m_llTimeStamp)
                return -1;

            UdtCongestionControl u = m_pHeap[0].m_pUDT;
            remove_(u);

            if (!u.m_bConnected || u.m_bBroken)
                return -1;

            // pack a packet from the socket
            if (u.packData(pkt, ref ts) <= 0)
                return -1;

            addr = u.m_pPeerAddr;

            // insert a new entry, ts is the next processing time
            if (ts > 0)
                insert_(ts, u);

            return 1;
        }
    }

    public void remove(UdtCongestionControl u)
    {
        lock (m_ListLock)
        {
            remove_(u);
        }
    }

    public ulong getNextProcTime()
    {
        lock (m_ListLock)
        {
            if (-1 == m_iLastEntry)
                return 0;

            return m_pHeap[0].m_llTimeStamp;
        }
    }

    void insert_(ulong ts, UdtCongestionControl u)
    {
        SNode n = u.m_pSNode;

        // do not insert repeated node
        if (n.m_iHeapLoc >= 0)
            return;

        m_iLastEntry++;
        m_pHeap[m_iLastEntry] = n;
        n.m_llTimeStamp = ts;

        int q = m_iLastEntry;
        int p = q;
        while (p != 0)
        {
            p = (q - 1) >> 1;
            if (m_pHeap[p].m_llTimeStamp > m_pHeap[q].m_llTimeStamp)
            {
                SNode t = m_pHeap[p];
                m_pHeap[p] = m_pHeap[q];
                m_pHeap[q] = t;
                t.m_iHeapLoc = q;
                q = p;
            }
            else
                break;
        }

        n.m_iHeapLoc = q;

        // an earlier event has been inserted, wake up sending worker
        if (n.m_iHeapLoc == 0)
            m_pTimer.interrupt();

        // first entry, activate the sending queue
        if (0 == m_iLastEntry)
        {
            m_pWindowCond.Set();
        }
    }

    void remove_(UdtCongestionControl u)
    {
        SNode n = u.m_pSNode;

        if (n.m_iHeapLoc >= 0)
        {
            // remove the node from heap
            m_pHeap[n.m_iHeapLoc] = m_pHeap[m_iLastEntry];
            m_iLastEntry--;
            m_pHeap[n.m_iHeapLoc].m_iHeapLoc = n.m_iHeapLoc;

            int q = n.m_iHeapLoc;
            int p = q * 2 + 1;
            while (p <= m_iLastEntry)
            {
                if ((p + 1 <= m_iLastEntry) && (m_pHeap[p].m_llTimeStamp > m_pHeap[p + 1].m_llTimeStamp))
                    p++;

                if (m_pHeap[q].m_llTimeStamp > m_pHeap[p].m_llTimeStamp)
                {
                    SNode t = m_pHeap[p];
                    m_pHeap[p] = m_pHeap[q];
                    m_pHeap[p].m_iHeapLoc = p;
                    m_pHeap[q] = t;
                    m_pHeap[q].m_iHeapLoc = q;

                    q = p;
                    p = q * 2 + 1;
                }
                else
                    break;
            }

            n.m_iHeapLoc = -1;
        }

        // the only event has been deleted, wake up immediately
        if (0 == m_iLastEntry)
            m_pTimer.interrupt();
    }
}


