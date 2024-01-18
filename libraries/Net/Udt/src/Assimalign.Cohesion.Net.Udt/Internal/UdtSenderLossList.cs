using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class UdtSenderLossList
{
    int[] m_piData1;                  // sequence number starts
    int[] m_piData2;                  // seqnence number ends
    int[] m_piNext;                       // next node in the list

    int m_iHead;                         // first node
    int m_iLength;                       // loss length
    int m_iSize;                         // size of the static array
    int m_iLastInsertPos;                // position of last insert node

    object m_ListLock = new object();          // used to synchronize list operation

    public UdtSenderLossList(int size)
    {
        m_iHead = -1;
        m_iLength = 0;
        m_iSize = size;
        m_iLastInsertPos = -1;

        m_piData1 = new int[m_iSize];
        m_piData2 = new int[m_iSize];
        m_piNext = new int[m_iSize];

        // -1 means there is no data in the node
        for (int i = 0; i < size; ++i)
        {
            m_piData1[i] = -1;
            m_piData2[i] = -1;
        }

    }

    public int insert(int seqno1, int seqno2)
    {
        lock (m_ListLock)
        {
            return insert_unsafe(seqno1, seqno2);
        }
    }

    int insert_unsafe(int seqno1, int seqno2)
    {
        if (0 == m_iLength)
        {
            // insert data into an empty list

            m_iHead = 0;
            m_piData1[m_iHead] = seqno1;
            if (seqno2 != seqno1)
                m_piData2[m_iHead] = seqno2;

            m_piNext[m_iHead] = -1;
            m_iLastInsertPos = m_iHead;

            m_iLength += UdtSequenceNumber.seqlen(seqno1, seqno2);

            return m_iLength;
        }

        // otherwise find the position where the data can be inserted
        int origlen = m_iLength;
        int offset = UdtSequenceNumber.seqoff(m_piData1[m_iHead], seqno1);
        int loc = (m_iHead + offset + m_iSize) % m_iSize;

        if (offset < 0)
        {
            // Insert data prior to the head pointer

            m_piData1[loc] = seqno1;
            if (seqno2 != seqno1)
                m_piData2[loc] = seqno2;

            // new node becomes head
            m_piNext[loc] = m_iHead;
            m_iHead = loc;
            m_iLastInsertPos = loc;

            m_iLength += UdtSequenceNumber.seqlen(seqno1, seqno2);
        }
        else if (offset > 0)
        {
            if (seqno1 == m_piData1[loc])
            {
                m_iLastInsertPos = loc;

                // first seqno is equivlent, compare the second
                if (-1 == m_piData2[loc])
                {
                    if (seqno2 != seqno1)
                    {
                        m_iLength += UdtSequenceNumber.seqlen(seqno1, seqno2) - 1;
                        m_piData2[loc] = seqno2;
                    }
                }
                else if (UdtSequenceNumber.seqcmp(seqno2, m_piData2[loc]) > 0)
                {
                    // new seq pair is longer than old pair, e.g., insert [3, 7] to [3, 5], becomes [3, 7]
                    m_iLength += UdtSequenceNumber.seqlen(m_piData2[loc], seqno2) - 1;
                    m_piData2[loc] = seqno2;
                }
                else
                    // Do nothing if it is already there
                    return 0;
            }
            else
            {
                // searching the prior node
                int i;
                if ((-1 != m_iLastInsertPos) && (UdtSequenceNumber.seqcmp(m_piData1[m_iLastInsertPos], seqno1) < 0))
                    i = m_iLastInsertPos;
                else
                    i = m_iHead;

                while ((-1 != m_piNext[i]) && (UdtSequenceNumber.seqcmp(m_piData1[m_piNext[i]], seqno1) < 0))
                    i = m_piNext[i];

                if ((-1 == m_piData2[i]) || (UdtSequenceNumber.seqcmp(m_piData2[i], seqno1) < 0))
                {
                    m_iLastInsertPos = loc;

                    // no overlap, create new node
                    m_piData1[loc] = seqno1;
                    if (seqno2 != seqno1)
                        m_piData2[loc] = seqno2;

                    m_piNext[loc] = m_piNext[i];
                    m_piNext[i] = loc;

                    m_iLength += UdtSequenceNumber.seqlen(seqno1, seqno2);
                }
                else
                {
                    m_iLastInsertPos = i;

                    // overlap, coalesce with prior node, insert(3, 7) to [2, 5], ... becomes [2, 7]
                    if (UdtSequenceNumber.seqcmp(m_piData2[i], seqno2) < 0)
                    {
                        m_iLength += UdtSequenceNumber.seqlen(m_piData2[i], seqno2) - 1;
                        m_piData2[i] = seqno2;

                        loc = i;
                    }
                    else
                        return 0;
                }
            }
        }
        else
        {
            m_iLastInsertPos = m_iHead;

            // insert to head node
            if (seqno2 != seqno1)
            {
                if (-1 == m_piData2[loc])
                {
                    m_iLength += UdtSequenceNumber.seqlen(seqno1, seqno2) - 1;
                    m_piData2[loc] = seqno2;
                }
                else if (UdtSequenceNumber.seqcmp(seqno2, m_piData2[loc]) > 0)
                {
                    m_iLength += UdtSequenceNumber.seqlen(m_piData2[loc], seqno2) - 1;
                    m_piData2[loc] = seqno2;
                }
                else
                    return 0;
            }
            else
                return 0;
        }

        // coalesce with next node. E.g., [3, 7], ..., [6, 9] becomes [3, 9] 
        while ((-1 != m_piNext[loc]) && (-1 != m_piData2[loc]))
        {
            int i = m_piNext[loc];

            if (UdtSequenceNumber.seqcmp(m_piData1[i], UdtSequenceNumber.incseq(m_piData2[loc])) <= 0)
            {
                // coalesce if there is overlap
                if (-1 != m_piData2[i])
                {
                    if (UdtSequenceNumber.seqcmp(m_piData2[i], m_piData2[loc]) > 0)
                    {
                        if (UdtSequenceNumber.seqcmp(m_piData2[loc], m_piData1[i]) >= 0)
                            m_iLength -= UdtSequenceNumber.seqlen(m_piData1[i], m_piData2[loc]);

                        m_piData2[loc] = m_piData2[i];
                    }
                    else
                        m_iLength -= UdtSequenceNumber.seqlen(m_piData1[i], m_piData2[i]);
                }
                else
                {
                    if (m_piData1[i] == UdtSequenceNumber.incseq(m_piData2[loc]))
                        m_piData2[loc] = m_piData1[i];
                    else
                        m_iLength--;
                }

                m_piData1[i] = -1;
                m_piData2[i] = -1;
                m_piNext[loc] = m_piNext[i];
            }
            else
                break;
        }

        return m_iLength - origlen;
    }

    public void remove(int seqno)
    {
        lock (m_ListLock)
        {
            remove_unsafe(seqno);
        }
    }

    void remove_unsafe(int seqno)
    {
        if (0 == m_iLength)
            return;

        // Remove all from the head pointer to a node with a larger seq. no. or the list is empty
        int offset = UdtSequenceNumber.seqoff(m_piData1[m_iHead], seqno);
        int loc = (m_iHead + offset + m_iSize) % m_iSize;

        if (0 == offset)
        {
            // It is the head. Remove the head and point to the next node
            loc = (loc + 1) % m_iSize;

            if (-1 == m_piData2[m_iHead])
                loc = m_piNext[m_iHead];
            else
            {
                m_piData1[loc] = UdtSequenceNumber.incseq(seqno);
                if (UdtSequenceNumber.seqcmp(m_piData2[m_iHead], UdtSequenceNumber.incseq(seqno)) > 0)
                    m_piData2[loc] = m_piData2[m_iHead];

                m_piData2[m_iHead] = -1;

                m_piNext[loc] = m_piNext[m_iHead];
            }

            m_piData1[m_iHead] = -1;

            if (m_iLastInsertPos == m_iHead)
                m_iLastInsertPos = -1;

            m_iHead = loc;

            m_iLength--;
        }
        else if (offset > 0)
        {
            int h = m_iHead;

            if (seqno == m_piData1[loc])
            {
                // target node is not empty, remove part/all of the seqno in the node.
                int temp = loc;
                loc = (loc + 1) % m_iSize;

                if (-1 == m_piData2[temp])
                    m_iHead = m_piNext[temp];
                else
                {
                    // remove part, e.g., [3, 7] becomes [], [4, 7] after remove(3)
                    m_piData1[loc] = UdtSequenceNumber.incseq(seqno);
                    if (UdtSequenceNumber.seqcmp(m_piData2[temp], m_piData1[loc]) > 0)
                        m_piData2[loc] = m_piData2[temp];
                    m_iHead = loc;
                    m_piNext[loc] = m_piNext[temp];
                    m_piNext[temp] = loc;
                    m_piData2[temp] = -1;
                }
            }
            else
            {
                // target node is empty, check prior node
                int i = m_iHead;
                while ((-1 != m_piNext[i]) && (UdtSequenceNumber.seqcmp(m_piData1[m_piNext[i]], seqno) < 0))
                    i = m_piNext[i];

                loc = (loc + 1) % m_iSize;

                if (-1 == m_piData2[i])
                    m_iHead = m_piNext[i];
                else if (UdtSequenceNumber.seqcmp(m_piData2[i], seqno) > 0)
                {
                    // remove part/all seqno in the prior node
                    m_piData1[loc] = UdtSequenceNumber.incseq(seqno);
                    if (UdtSequenceNumber.seqcmp(m_piData2[i], m_piData1[loc]) > 0)
                        m_piData2[loc] = m_piData2[i];

                    m_piData2[i] = seqno;

                    m_piNext[loc] = m_piNext[i];
                    m_piNext[i] = loc;

                    m_iHead = loc;
                }
                else
                    m_iHead = m_piNext[i];
            }

            // Remove all nodes prior to the new head
            while (h != m_iHead)
            {
                if (m_piData2[h] != -1)
                {
                    m_iLength -= UdtSequenceNumber.seqlen(m_piData1[h], m_piData2[h]);
                    m_piData2[h] = -1;
                }
                else
                    m_iLength--;

                m_piData1[h] = -1;

                if (m_iLastInsertPos == h)
                    m_iLastInsertPos = -1;

                h = m_piNext[h];
            }
        }
    }

    public int getLossLength()
    {
        lock (m_ListLock)
        {
            return m_iLength;
        }
    }

    public int getLostSeq()
    {
        if (0 == m_iLength)
            return -1;

        lock (m_ListLock)
        {

            if (0 == m_iLength)
                return -1;

            if (m_iLastInsertPos == m_iHead)
                m_iLastInsertPos = -1;

            // return the first loss seq. no.
            int seqno = m_piData1[m_iHead];

            // head moves to the next node
            if (-1 == m_piData2[m_iHead])
            {
                //[3, -1] becomes [], and head moves to next node in the list
                m_piData1[m_iHead] = -1;
                m_iHead = m_piNext[m_iHead];
            }
            else
            {
                // shift to next node, e.g., [3, 7] becomes [], [4, 7]
                int loc = (m_iHead + 1) % m_iSize;

                m_piData1[loc] = UdtSequenceNumber.incseq(seqno);
                if (UdtSequenceNumber.seqcmp(m_piData2[m_iHead], m_piData1[loc]) > 0)
                    m_piData2[loc] = m_piData2[m_iHead];

                m_piData1[m_iHead] = -1;
                m_piData2[m_iHead] = -1;

                m_piNext[loc] = m_piNext[m_iHead];
                m_iHead = loc;
            }

            m_iLength--;

            return seqno;
        }
    }
}
