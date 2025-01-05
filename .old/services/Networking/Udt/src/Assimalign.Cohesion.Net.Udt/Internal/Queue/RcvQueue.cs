using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class RcvQueue
{
    RcvUList m_pRcvUList = new RcvUList();     // List of UDT instances that will read packets from the queue
    UdtChannel m_pChannel;       // UDP channel for receving packets
    UdtTimer m_pTimer;           // shared timer with the snd queue

    int m_iPayloadSize;                  // packet payload size

    volatile bool m_bClosing;            // closing the workder
    EventWaitHandle m_ExitCond;

    object m_LSLock;
    UdtCongestionControl m_pListener;                                   // pointer to the (unique, if any) listening UDT entity
    RendezvousQueue m_pRendezvousQueue = new RendezvousQueue();                // The list of sockets in rendezvous mode

    List<UdtCongestionControl> m_vNewEntry = new List<UdtCongestionControl>();                      // newly added entries, to be inserted
    object m_IDLock;

    Dictionary<int, Queue<UdtPacket>> m_mBuffer = new Dictionary<int, Queue<UdtPacket>>();  // temporary buffer for rendezvous connection request

    object m_PassLock;
    EventWaitHandle m_PassCond;

    Thread m_WorkerThread;

    Dictionary<int, UdtCongestionControl> m_hash = new Dictionary<int, UdtCongestionControl>();

    public RcvQueue()
    {
        m_PassLock = new object();
        m_PassCond = new EventWaitHandle(false, EventResetMode.AutoReset);
        m_LSLock = new object();
        m_IDLock = new object();
        m_ExitCond = new EventWaitHandle(false, EventResetMode.AutoReset);
    }

    public void Close()
    {
        m_bClosing = true;

        if (null != m_WorkerThread)
            m_ExitCond.WaitOne(Timeout.Infinite);

        m_PassCond.Close();
        m_ExitCond.Close();
    }

    public void init(int qsize, int payload, AddressFamily version, int hsize, UdtChannel cc, UdtTimer t)
    {
        m_iPayloadSize = payload;

        m_pChannel = cc;
        m_pTimer = t;

        m_WorkerThread = new Thread(worker);
        m_WorkerThread.IsBackground = true;
        m_WorkerThread.Start(this);
    }

    static void worker(object param)
    {
        RcvQueue self = param as RcvQueue;
        if (self == null)
            return;

        IPEndPoint addr = new IPEndPoint(IPAddress.Any, 0);
        UdtCongestionControl u = null;
        int id;

        while (!self.m_bClosing)
        {
            self.m_pTimer.tick();

            // check waiting list, if new socket, insert it to the list
            while (self.ifNewEntry())
            {
                UdtCongestionControl ne = self.getNewEntry();
                if (null != ne)
                {
                    self.m_pRcvUList.insert(ne);
                    self.m_hash.Add(ne.m_SocketID, ne);
                }
            }

            // find next available slot for incoming packet
            UdtUnit unit = new UdtUnit();
            unit.m_Packet.setLength(self.m_iPayloadSize);

            // reading next incoming packet, recvfrom returns -1 is nothing has been received
            if (self.m_pChannel.recvfrom(ref addr, unit.m_Packet) < 0)
                goto TIMER_CHECK;

            id = unit.m_Packet.GetId();

            // ID 0 is for connection request, which should be passed to the listening socket or rendezvous sockets
            if (0 == id)
            {
                if (null != self.m_pListener)
                    self.m_pListener.listen(addr, unit.m_Packet);
                else if (null != (u = self.m_pRendezvousQueue.retrieve(addr, ref id)))
                {
                    // asynchronous connect: call connect here
                    // otherwise wait for the UDT socket to retrieve this packet
                    if (!u.m_bSynRecving)
                        u.connect(unit.m_Packet);
                    else
                    {
                        UdtPacket newPacket = new UdtPacket();
                        newPacket.Clone(unit.m_Packet);
                        self.storePkt(id, newPacket);
                    }
                }
            }
            else if (id > 0)
            {
                if (self.m_hash.TryGetValue(id, out u))
                {
                    if (addr.Equals(u.m_pPeerAddr))
                    {
                        if (u.m_bConnected && !u.m_bBroken && !u.m_bClosing)
                        {
                            if (0 == unit.m_Packet.getFlag())
                                u.processData(unit);
                            else
                                u.processCtrl(unit.m_Packet);

                            u.checkTimers();
                            self.m_pRcvUList.update(u);
                        }
                    }
                }
                else if (null != (u = self.m_pRendezvousQueue.retrieve(addr, ref id)))
                {
                    if (!u.m_bSynRecving)
                        u.connect(unit.m_Packet);
                    else
                    {
                        UdtPacket newPacket = new UdtPacket();
                        newPacket.Clone(unit.m_Packet);
                        self.storePkt(id, newPacket);
                    }
                }
            }

        TIMER_CHECK:
            // take care of the timing event for all UDT sockets

            ulong currtime = UdtTimer.rdtsc();

            ulong ctime = currtime - 100000 * UdtTimer.getCPUFrequency();
            for (int i = 0; i < self.m_pRcvUList.m_nodeList.Count; ++i)
            {
                RNode ul = self.m_pRcvUList.m_nodeList[0];
                if (ul.m_llTimeStamp >= ctime)
                    break;

                u = ul.m_pUDT;

                if (u.m_bConnected && !u.m_bBroken && !u.m_bClosing)
                {
                    u.checkTimers();
                    self.m_pRcvUList.update(u);
                }
                else
                {
                    // the socket must be removed from Hash table first, then RcvUList
                    self.m_hash.Remove(u.m_SocketID);
                    self.m_pRcvUList.remove(u);
                    u.m_pRNode.m_bOnList = false;
                }
            }

            // Check connection requests status for all sockets in the RendezvousQueue.
            self.m_pRendezvousQueue.updateConnStatus();
        }


        self.m_ExitCond.Set();
    }

    public int recvfrom(int id, UdtPacket packet)
    {
        bool gotLock = false;
        Monitor.Enter(m_PassLock, ref gotLock);

        Queue<UdtPacket> packetQueue;
        if (!m_mBuffer.TryGetValue(id, out packetQueue))
        {
            if (gotLock)
                Monitor.Exit(m_PassLock);
            m_PassCond.WaitOne(1000);

            lock (m_PassLock)
            {

                if (!m_mBuffer.TryGetValue(id, out packetQueue))
                {
                    packet.setLength(-1);
                    return -1;
                }
            }
        }

        if (gotLock && Monitor.IsEntered(m_PassLock))
            Monitor.Exit(m_PassLock);

        // retrieve the earliest packet
        UdtPacket newpkt = packetQueue.Peek();

        if (packet.getLength() < newpkt.getLength())
        {
            packet.setLength(-1);
            return -1;
        }

        // copy packet content

        packet.Clone(newpkt);

        packetQueue.Dequeue();
        if (packetQueue.Count == 0)
        {
            lock (m_PassLock)
            {
                m_mBuffer.Remove(id);
            }
        }

        return packet.getLength();
    }

    public int setListener(UdtCongestionControl u)
    {
        lock (m_LSLock)
        {

            if (null != m_pListener)
                return -1;

            m_pListener = u;
            return 0;
        }
    }

    public void removeListener(UdtCongestionControl u)
    {
        lock (m_LSLock)
        {
            if (u == m_pListener)
                m_pListener = null;
        }
    }

    public void registerConnector(int id, UdtCongestionControl u, AddressFamily ipv, IPEndPoint addr, ulong ttl)
    {
        m_pRendezvousQueue.insert(id, u, ipv, addr, ttl);
    }

    public void removeConnector(int id)
    {
        m_pRendezvousQueue.remove(id);
        lock (m_PassLock)
        {
            m_mBuffer.Remove(id);
        }
    }

    public void setNewEntry(UdtCongestionControl u)
    {
        lock (m_IDLock)
        {
            m_vNewEntry.Add(u);
        }
    }

    bool ifNewEntry()
    {
        return !(m_vNewEntry.Count == 0);
    }

    UdtCongestionControl getNewEntry()
    {
        lock (m_IDLock)
        {
            if (m_vNewEntry.Count == 0)
                return null;

            UdtCongestionControl u = m_vNewEntry[0];
            m_vNewEntry.RemoveAt(0);
            return u;
        }
    }

    void storePkt(int id, UdtPacket pkt)
    {
        lock (m_PassLock)
        {
            Queue<UdtPacket> packetQueue;
            if (!m_mBuffer.TryGetValue(id, out packetQueue))
            {
                packetQueue = new Queue<UdtPacket>();
                packetQueue.Enqueue(pkt);
                m_mBuffer.Add(id, packetQueue);

                m_PassCond.Set();
            }
            else
            {
                //avoid storing too many packets, in case of malfunction or attack
                if (packetQueue.Count > 16)
                    return;

                packetQueue.Enqueue(pkt);
            }
        }
    }
}
