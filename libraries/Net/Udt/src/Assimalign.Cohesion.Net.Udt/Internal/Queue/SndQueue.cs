using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class SndQueue
{
    public SndUList m_pSndUList;     // List of UDT instances for data sending
    public UdtChannel m_pChannel;                // The UDP channel for data sending
    UdtTimer m_pTimer;           // Timing facility

    object m_WindowLock;
    EventWaitHandle m_WindowCond;

    volatile bool m_bClosing;       // closing the worker
    EventWaitHandle m_ExitCond;

    Thread m_WorkerThread;

    public SndQueue()
    {
        m_WindowLock = new object();
        m_WindowCond = new EventWaitHandle(false, EventResetMode.AutoReset);
        m_ExitCond = new EventWaitHandle(false, EventResetMode.AutoReset);
    }

    public void Close()
    {
        m_bClosing = true;

        m_WindowCond.Set();
        if (null != m_WorkerThread)
            m_ExitCond.WaitOne(Timeout.Infinite);

        m_WindowCond.Close();
        m_ExitCond.Close();
    }

    public void init(UdtChannel c, UdtTimer t)
    {
        m_pChannel = c;
        m_pTimer = t;
        m_pSndUList = new SndUList();
        m_pSndUList.m_pWindowLock = m_WindowLock;
        m_pSndUList.m_pWindowCond = m_WindowCond;
        m_pSndUList.m_pTimer = m_pTimer;

        m_WorkerThread = new Thread(worker);
        m_WorkerThread.IsBackground = true;
        m_WorkerThread.Start(this);
    }

    static void worker(object param)
    {
        SndQueue self = param as SndQueue;
        if (self == null)
            return;

        while (!self.m_bClosing)
        {
            ulong ts = self.m_pSndUList.getNextProcTime();

            if (ts > 0)
            {
                // wait until next processing time of the first socket on the list
                ulong currtime = UdtTimer.rdtsc();
                if (currtime < ts)
                    self.m_pTimer.sleepto(ts);

                // it is time to send the next pkt
                IPEndPoint addr = null;
                UdtPacket pkt = new UdtPacket();
                if (self.m_pSndUList.pop(ref addr, ref pkt) < 0)
                    continue;

                self.m_pChannel.sendto(addr, pkt);
            }
            else
            {
                // wait here if there is no sockets with data to be sent
                self.m_WindowCond.WaitOne(Timeout.Infinite);
            }
        }

        self.m_ExitCond.Set();
    }

    public int sendto(IPEndPoint addr, UdtPacket packet)
    {
        // send out the packet immediately (high priority), this is a control packet
        m_pChannel.sendto(addr, packet);
        return packet.getLength();
    }
}

