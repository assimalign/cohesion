using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class RendezvousQueue
{
    struct CRL
    {
        internal int m_iID;            // UDT socket ID (self)
        internal UdtCongestionControl m_pUDT;           // UDT instance
        internal AddressFamily m_iIPversion;                 // IP version
        internal IPEndPoint m_pPeerAddr;      // UDT sonnection peer address
        internal ulong m_ullTTL;          // the time that this request expires
    };
    List<CRL> m_lRendezvousID = new List<CRL>();      // The sockets currently in rendezvous mode

    object m_RIDVectorLock = new object();

    public void insert(int id, UdtCongestionControl u, AddressFamily ipv, IPEndPoint addr, ulong ttl)
    {
        CRL r;
        r.m_iID = id;
        r.m_pUDT = u;
        r.m_iIPversion = ipv;
        r.m_pPeerAddr = addr;
        r.m_ullTTL = ttl;

        lock (m_RIDVectorLock)
        {
            m_lRendezvousID.Add(r);
        }
    }

    public void remove(int id)
    {
        lock (m_RIDVectorLock)
        {
            for (int i = 0; i < m_lRendezvousID.Count; ++i)
            {
                if (m_lRendezvousID[i].m_iID == id)
                {
                    m_lRendezvousID.RemoveAt(i);
                    return;
                }
            }
        }
    }

    public UdtCongestionControl retrieve(IPEndPoint addr, ref int id)
    {
        lock (m_RIDVectorLock)
        {
            foreach (CRL crl in m_lRendezvousID)
            {
                if (crl.m_pPeerAddr.Equals(addr) && (id == 0) || (id == crl.m_iID))
                {
                    id = crl.m_iID;
                    return crl.m_pUDT;
                }
            }

            return null;
        }
    }

    public void updateConnStatus()
    {
        if (m_lRendezvousID.Count == 0)
            return;

        lock (m_RIDVectorLock)
        {

            foreach (CRL crl in m_lRendezvousID)
            {
                // avoid sending too many requests, at most 1 request per 250ms
                if (UdtTimer.getTime() - (ulong)crl.m_pUDT.m_llLastReqTime > 250000)
                {
                    //if (Timer.getTime() >= crl.m_ullTTL)
                    //{
                    //    // connection timer expired, acknowledge app via epoll
                    //    i->m_pUDT->m_bConnecting = false;
                    //    CUDT::s_UDTUnited.m_EPoll.update_events(i->m_iID, i->m_pUDT->m_sPollID, UDT_EPOLL_ERR, true);
                    //    continue;
                    //}

                    UdtPacket request = new UdtPacket();
                    request.pack(crl.m_pUDT.m_ConnReq);
                    // ID = 0, connection request
                    request.SetId(!crl.m_pUDT.m_bRendezvous ? 0 : crl.m_pUDT.m_ConnRes.SocketId);
                    crl.m_pUDT.m_pSndQueue.sendto(crl.m_pPeerAddr, request);
                    crl.m_pUDT.m_llLastReqTime = (long)UdtTimer.getTime();
                }
            }
        }
    }

}
