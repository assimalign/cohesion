using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class UnitQueue
{
    struct QEntry
    {
        internal UdtUnit[] m_pUnit;     // unit queue
        internal byte[][] m_pBuffer;        // data buffer
        internal int m_iSize;        // size of each queue
    }
    List<QEntry> mEntries = new List<QEntry>();

    int m_iCurrEntry = 0;
    int m_iLastEntry = 0;

    int m_iAvailUnit;         // recent available unit
    int m_iAvailableQueue;

    int m_iSize;            // total size of the unit queue, in number of packets
    public int m_iCount;       // total number of valid packets in the queue

    int m_iMSS;         // unit buffer size
    AddressFamily m_iIPversion;  // IP version

    UnitQueue()
    {
        m_iSize = 0;
        m_iCount = 0;
        m_iMSS = 0;
        m_iIPversion = 0;
    }

    ~UnitQueue()
    {
    }

    int init(int size, int mss, AddressFamily version)
    {
        QEntry tempq = new QEntry();
        UdtUnit[] tempu = new UdtUnit[size];
        byte[][] tempb = new byte[size][];

        for (int i = 0; i < size; ++i)
        {
            tempb[i] = new byte[mss];
            tempu[i] = new UdtUnit();
            tempu[i].m_iFlag = 0;

            tempu[i].m_Packet.SetDataFromBytes(tempb[i]);
        }
        tempq.m_pUnit = tempu;
        tempq.m_pBuffer = tempb;
        tempq.m_iSize = size;

        m_iSize = size;
        m_iMSS = mss;
        m_iIPversion = version;

        mEntries.Add(tempq);

        return 0;
    }

    int increase()
    {
        // adjust/correct m_iCount
        int real_count = 0;
        for (int q = 0; q < mEntries.Count; ++q)
        {
            UdtUnit[] units = mEntries[q].m_pUnit;
            for (int u = mEntries[q].m_iSize; u < units.Length; ++u)
                if (units[u].m_iFlag != 0)
                    ++real_count;
        }
        m_iCount = real_count;
        if ((double)m_iCount / m_iSize < 0.9)
            return -1;

        // all queues have the same size
        int size = mEntries[0].m_iSize;

        QEntry tempq = new QEntry();
        UdtUnit[] tempu = new UdtUnit[size];
        byte[][] tempb = new byte[size][];

        for (int i = 0; i < size; ++i)
        {
            tempb[i] = new byte[m_iMSS];
            tempu[i].m_iFlag = 0;
            tempu[i].m_Packet.SetDataFromBytes(tempb[i]);
        }
        tempq.m_pUnit = tempu;
        tempq.m_pBuffer = tempb;
        tempq.m_iSize = size;

        mEntries.Add(tempq);

        m_iSize += size;

        return 0;
    }

    int shrink()
    {
        // currently queue cannot be shrunk.
        return -1;
    }

    UdtUnit getNextAvailUnit()
    {
        if (m_iCount * 10 > m_iSize * 9)
            increase();

        if (m_iCount >= m_iSize)
            return null;

        QEntry entrance = mEntries[m_iCurrEntry];

        //do
        //{
        //    QEntry currentEntry = mEntries[m_iCurrEntry];
        //    Unit sentinel = currentEntry.m_pUnit[currentEntry.m_iSize - 1];
        //    for (CUnit* sentinel = m_pCurrQueue.m_pUnit + m_pCurrQueue.m_iSize - 1; m_pAvailUnit != sentinel; ++m_pAvailUnit)
        //        if (m_pAvailUnit.m_iFlag == 0)
        //            return m_pAvailUnit;



        //    if (m_pCurrQueue.m_pUnit.m_iFlag == 0)
        //    {
        //        m_pAvailUnit = m_pCurrQueue.m_pUnit;
        //        return m_pAvailUnit;
        //    }

        //    m_pCurrQueue = m_pCurrQueue.m_pNext;
        //    m_pAvailUnit = m_pCurrQueue.m_pUnit;
        //} while (m_pCurrQueue != entrance);

        increase();

        return null;
    }
}
