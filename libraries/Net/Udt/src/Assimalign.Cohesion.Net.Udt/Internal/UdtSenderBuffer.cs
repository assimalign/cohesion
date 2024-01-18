using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class UdtSenderBuffer
{
    object m_BufLock = new object();           // used to synchronize buffer operation

    class Block
    {
        internal byte[] m_pcData;                   // pointer to the data block
        internal int m_iLength;                    // length of the block

        internal uint m_iMsgNo;                 // message number
        internal ulong m_OriginTime;            // original request time
        internal int m_iTTL;                       // time to live (milliseconds)
    }

    List<Block> mBlockList = new List<Block>();
    int m_iLastBlock = 0;
    int m_iCurrentBlock = 0;
    int m_iFirstBlock = 0;

    uint m_iNextMsgNo;                // next message number

    int m_iSize;                // buffer size (number of packets)
    int m_iMSS;                          // maximum seqment/packet size

    int m_iCount;           // number of used blocks

    public UdtSenderBuffer(int size, int mss)
    {
        m_iSize = size;
        m_iMSS = mss;

        // circular linked list for out bound packets

        for (int i = 0; i < m_iSize; ++i)
        {
            Block block = new Block();
            block.m_iMsgNo = 0;
            block.m_pcData = new byte[m_iMSS];
            mBlockList.Add(block);
        }

    }

    // Functionality:
    //    Insert a user buffer into the sending list.
    // Parameters:
    //    0) [in] data: pointer to the user data block.
    //    1) [in] len: size of the block.
    //    2) [in] ttl: time to live in milliseconds
    //    3) [in] order: if the block should be delivered in order, for DGRAM only
    // Returned value:
    //    None.
    public void addBuffer(byte[] data, int offset, int len, int ttl = -1, bool order = false)
    {
        int size = len / m_iMSS;
        if ((len % m_iMSS) != 0)
            size++;

        // dynamically increase sender buffer
        while (size + m_iCount >= m_iSize)
            increase();

        ulong time = UdtTimer.getTime();
        uint inorder = Convert.ToUInt32(order);
        inorder <<= 29;

        for (int i = 0; i < size; ++i)
        {
            Block s = mBlockList[m_iLastBlock];
            IncrementBlockIndex(ref m_iLastBlock);
            int pktlen = len - i * m_iMSS;
            if (pktlen > m_iMSS)
                pktlen = m_iMSS;

            Array.Copy(data, i * m_iMSS + offset, s.m_pcData, 0, pktlen);
            s.m_iLength = pktlen;
            s.m_iMsgNo = m_iNextMsgNo | inorder;
            if (i == 0)
                s.m_iMsgNo |= 0x80000000;
            if (i == size - 1)
                s.m_iMsgNo |= 0x40000000;

            s.m_OriginTime = time;
            s.m_iTTL = ttl;
        }

        lock (m_BufLock)
        {
            m_iCount += size;
        }

        m_iNextMsgNo++;
        if (m_iNextMsgNo == UdtMessageNumber.m_iMaxMsgNo)
            m_iNextMsgNo = 1;
    }

    public int readData(ref byte[] data, ref uint msgno)
    {
        // No data to read
        if (m_iCurrentBlock == m_iLastBlock)
            return 0;

        data = mBlockList[m_iCurrentBlock].m_pcData;
        int readlen = mBlockList[m_iCurrentBlock].m_iLength;
        msgno = mBlockList[m_iCurrentBlock].m_iMsgNo;

        IncrementBlockIndex(ref m_iCurrentBlock);

        return readlen;
    }

    public int readData(ref byte[] data, int offset, ref uint msgno, out int msglen)
    {
        msglen = 0;
        lock (m_BufLock)
        {
            int blockIndex = m_iFirstBlock;
            IncrementBlockIndex(ref blockIndex, offset);
            Block p = mBlockList[blockIndex];

            if ((p.m_iTTL >= 0) && ((UdtTimer.getTime() - p.m_OriginTime) / 1000 > (ulong)p.m_iTTL))
            {
                msgno = p.m_iMsgNo & 0x1FFFFFFF;

                msglen = 1;

                IncrementBlockIndex(ref blockIndex);
                p = mBlockList[blockIndex];

                bool move = false;
                while (msgno == (p.m_iMsgNo & 0x1FFFFFFF))
                {
                    if (blockIndex == m_iCurrentBlock)
                        move = true;

                    IncrementBlockIndex(ref blockIndex);
                    p = mBlockList[blockIndex];

                    if (move)
                        m_iCurrentBlock = blockIndex;
                    msglen++;
                }

                return -1;
            }

            data = p.m_pcData;
            int readlen = p.m_iLength;
            msgno = p.m_iMsgNo;

            return readlen;
        }
    }

    void IncrementBlockIndex(ref int blockIndex, int offset = 1)
    {
        blockIndex = (blockIndex + offset) % mBlockList.Count;
    }

    public void ackData(int offset)
    {
        lock (m_BufLock)
        {
            IncrementBlockIndex(ref m_iFirstBlock, offset);

            m_iCount -= offset;

            UdtTimer.triggerEvent();
        }
    }

    public int getCurrBufSize()
    {
        return m_iCount;
    }

    void increase()
    {
        int unitsize = m_iSize;

        for (int i = 0; i < unitsize; ++i)
        {
            Block block = new Block();
            block.m_iMsgNo = 0;
            block.m_pcData = new byte[m_iMSS];
            mBlockList.Add(block);
        }

        m_iSize += unitsize;
    }
}
