using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Net.Udt.Internal;


internal class UdtReceiverBuffer
{
    UdtUnit[] m_pUnit;                     // pointer to the protocol buffer
    int m_iSize;                         // size of the protocol buffer

    int m_iStartPos;                     // the head position for I/O (inclusive)
    int m_iLastAckPos;                   // the last ACKed position (exclusive)
                                         // EMPTY: m_iStartPos = m_iLastAckPos   FULL: m_iStartPos = m_iLastAckPos + 1
    int m_iMaxPos;          // the furthest data position

    int m_iNotch;           // the starting read point of the first unit

    public UdtReceiverBuffer(int bufsize)
    {
        m_iSize = bufsize;
        m_iStartPos = 0;
        m_iLastAckPos = 0;
        m_iMaxPos = 0;
        m_iNotch = 0;
        m_pUnit = new UdtUnit[m_iSize];
        for (int i = 0; i < m_iSize; ++i)
            m_pUnit[i] = null;
    }

    ~UdtReceiverBuffer()
    {
        for (int i = 0; i < m_iSize; ++i)
        {
            if (null != m_pUnit[i])
            {
                m_pUnit[i].m_iFlag = 0;
            }
        }
    }

    public int addData(UdtUnit unit, int offset)
    {
        int pos = (m_iLastAckPos + offset) % m_iSize;
        if (offset > m_iMaxPos)
            m_iMaxPos = offset;

        if (null != m_pUnit[pos])
            return -1;

        m_pUnit[pos] = unit;

        unit.m_iFlag = 1;

        return 0;
    }

    public int readBuffer(byte[] data, int offset, int len)
    {
        int p = m_iStartPos;
        int lastack = m_iLastAckPos;
        int rs = len;

        while ((p != lastack) && (rs > 0))
        {
            int unitsize = m_pUnit[p].m_Packet.getLength() - m_iNotch;
            if (unitsize > rs)
                unitsize = rs;

            unitsize = m_pUnit[p].m_Packet.GetDataBytes(m_iNotch, data, offset, unitsize);

            offset += unitsize;

            if ((rs > unitsize) || (rs == m_pUnit[p].m_Packet.getLength() - m_iNotch))
            {
                UdtUnit tmp = m_pUnit[p];
                m_pUnit[p] = null;
                tmp.m_iFlag = 0;

                if (++p == m_iSize)
                    p = 0;

                m_iNotch = 0;
            }
            else
                m_iNotch += rs;

            rs -= unitsize;
        }

        m_iStartPos = p;
        return len - rs;
    }

    public void ackData(int len)
    {
        m_iLastAckPos = (m_iLastAckPos + len) % m_iSize;
        m_iMaxPos -= len;
        if (m_iMaxPos < 0)
            m_iMaxPos = 0;

        UdtTimer.triggerEvent();
    }

    public int getAvailBufSize()
    {
        // One slot must be empty in order to tell the difference between "empty buffer" and "full buffer"
        return m_iSize - getRcvDataSize() - 1;
    }

    public int getRcvDataSize()
    {
        if (m_iLastAckPos >= m_iStartPos)
            return m_iLastAckPos - m_iStartPos;

        return m_iSize + m_iLastAckPos - m_iStartPos;
    }

    public void dropMsg(int msgno)
    {
        for (int i = m_iStartPos, n = (m_iLastAckPos + m_iMaxPos) % m_iSize; i != n; i = (i + 1) % m_iSize)
            if ((null != m_pUnit[i]) && (msgno == m_pUnit[i].m_Packet.GetMessageNumber()))
                m_pUnit[i].m_iFlag = 3;
    }

    public int readMsg(byte[] data, int len)
    {
        int p = 0;
        int q = 0;
        bool passack = false;
        if (!scanMsg(ref p, ref q, ref passack))
            return 0;

        int rs = len;
        int dataOffset = 0;
        while (p != (q + 1) % m_iSize)
        {
            byte[] allData = m_pUnit[p].m_Packet.GetDataBytes();
            int unitsize = allData.Length;
            if ((rs >= 0) && (unitsize > rs))
                unitsize = rs;

            if (unitsize > 0)
            {
                Array.Copy(allData, 0, data, dataOffset, unitsize);
                dataOffset += unitsize;
                rs -= unitsize;
            }

            if (!passack)
            {
                UdtUnit tmp = m_pUnit[p];
                m_pUnit[p] = null;
                tmp.m_iFlag = 0;
            }
            else
                m_pUnit[p].m_iFlag = 2;

            if (++p == m_iSize)
                p = 0;
        }

        if (!passack)
            m_iStartPos = (q + 1) % m_iSize;

        return len - rs;
    }

    int getRcvMsgNum()
    {
        int p = 0;
        int q = 0;
        bool passack = false;
        return scanMsg(ref p, ref q, ref passack) ? 1 : 0;
    }

    bool scanMsg(ref int p, ref int q, ref bool passack)
    {   
        // empty buffer
        if ((m_iStartPos == m_iLastAckPos) && (m_iMaxPos <= 0))
            return false;

        //skip all bad msgs at the beginning
        while (m_iStartPos != m_iLastAckPos)
        {
            if (null == m_pUnit[m_iStartPos])
            {
                if (++m_iStartPos == m_iSize)
                    m_iStartPos = 0;
                continue;
            }

            if ((1 == m_pUnit[m_iStartPos].m_iFlag) && (m_pUnit[m_iStartPos].m_Packet.getMsgBoundary() > 1))
            {
                bool good = true;

                // look ahead for the whole message
                for (int i = m_iStartPos; i != m_iLastAckPos;)
                {
                    if ((null == m_pUnit[i]) || (1 != m_pUnit[i].m_iFlag))
                    {
                        good = false;
                        break;
                    }

                    if ((m_pUnit[i].m_Packet.getMsgBoundary() == 1) || (m_pUnit[i].m_Packet.getMsgBoundary() == 3))
                        break;

                    if (++i == m_iSize)
                        i = 0;
                }

                if (good)
                    break;
            }

            UdtUnit tmp = m_pUnit[m_iStartPos];
            m_pUnit[m_iStartPos] = null;
            tmp.m_iFlag = 0;

            if (++m_iStartPos == m_iSize)
                m_iStartPos = 0;
        }

        p = -1;                  // message head
        q = m_iStartPos;         // message tail
        passack = m_iStartPos == m_iLastAckPos;
        bool found = false;

        // looking for the first message
        for (int i = 0, n = m_iMaxPos + getRcvDataSize(); i <= n; ++i)
        {
            if ((null != m_pUnit[q]) && (1 == m_pUnit[q].m_iFlag))
            {
                switch (m_pUnit[q].m_Packet.getMsgBoundary())
                {
                    case 3: // 11
                        p = q;
                        found = true;
                        break;

                    case 2: // 10
                        p = q;
                        break;

                    case 1: // 01
                        if (p != -1)
                            found = true;
                        break;
                }
            }
            else
            {
                // a hole in this message, not valid, restart search
                p = -1;
            }

            if (found)
            {
                // the msg has to be ack'ed or it is allowed to read out of order, and was not read before
                if (!passack || !m_pUnit[q].m_Packet.getMsgOrderFlag())
                    break;

                found = false;
            }

            if (++q == m_iSize)
                q = 0;

            if (q == m_iLastAckPos)
                passack = true;
        }

        // no msg found
        if (!found)
        {
            // if the message is larger than the receiver buffer, return part of the message
            if ((p != -1) && ((q + 1) % m_iSize == p))
                found = true;
        }

        return found;
    }
}
