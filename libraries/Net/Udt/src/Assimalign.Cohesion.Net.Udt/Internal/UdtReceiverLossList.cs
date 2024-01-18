namespace Assimalign.Cohesion.Net.Udt.Internal;


internal class UdtReceiverLossList
{

    int[] m_piData1;                  // sequence number starts
    int[] m_piData2;                  // sequence number ends
    int[] m_piNext;                       // next node in the list
    int[] m_piPrior;                      // prior node in the list;

    int m_iHead;                         // first node in the list
    int m_iTail;                         // last node in the list;
    int m_iLength;                       // loss length
    int m_iSize;                         // size of the static array

    public UdtReceiverLossList(int size)
    {
        m_iHead = -1;
        m_iTail = -1;
        m_iLength = 0;
        m_iSize = size;
        m_piData1 = new int[m_iSize];
        m_piData2 = new int[m_iSize];
        m_piNext = new int[m_iSize];
        m_piPrior = new int[m_iSize];

        // -1 means there is no data in the node
        for (int i = 0; i < size; ++i)
        {
            m_piData1[i] = -1;
            m_piData2[i] = -1;
        }
    }


    public void insert(int seqno1, int seqno2)
    {
        // Data to be inserted must be larger than all those in the list
        // guaranteed by the UDT receiver

        if (0 == m_iLength)
        {
            // insert data into an empty list
            m_iHead = 0;
            m_iTail = 0;
            m_piData1[m_iHead] = seqno1;
            if (seqno2 != seqno1)
                m_piData2[m_iHead] = seqno2;

            m_piNext[m_iHead] = -1;
            m_piPrior[m_iHead] = -1;
            m_iLength += UdtSequenceNumber.seqlen(seqno1, seqno2);

            return;
        }

        // otherwise searching for the position where the node should be
        int offset = UdtSequenceNumber.seqoff(m_piData1[m_iHead], seqno1);
        int loc = (m_iHead + offset) % m_iSize;

        if ((-1 != m_piData2[m_iTail]) && (UdtSequenceNumber.incseq(m_piData2[m_iTail]) == seqno1))
        {
            // coalesce with prior node, e.g., [2, 5], [6, 7] becomes [2, 7]
            loc = m_iTail;
            m_piData2[loc] = seqno2;
        }
        else
        {
            // create new node
            m_piData1[loc] = seqno1;

            if (seqno2 != seqno1)
                m_piData2[loc] = seqno2;

            m_piNext[m_iTail] = loc;
            m_piPrior[loc] = m_iTail;
            m_piNext[loc] = -1;
            m_iTail = loc;
        }

        m_iLength += UdtSequenceNumber.seqlen(seqno1, seqno2);
    }

    public bool remove(int seqno)
    {
        if (0 == m_iLength)
            return false;

        // locate the position of "seqno" in the list
        int offset = UdtSequenceNumber.seqoff(m_piData1[m_iHead], seqno);
        if (offset < 0)
            return false;

        int loc = (m_iHead + offset) % m_iSize;

        if (seqno == m_piData1[loc])
        {
            // This is a seq. no. that starts the loss sequence

            if (-1 == m_piData2[loc])
            {
                // there is only 1 loss in the sequence, delete it from the node
                if (m_iHead == loc)
                {
                    m_iHead = m_piNext[m_iHead];
                    if (-1 != m_iHead)
                        m_piPrior[m_iHead] = -1;
                }
                else
                {
                    m_piNext[m_piPrior[loc]] = m_piNext[loc];
                    if (-1 != m_piNext[loc])
                        m_piPrior[m_piNext[loc]] = m_piPrior[loc];
                    else
                        m_iTail = m_piPrior[loc];
                }

                m_piData1[loc] = -1;
            }
            else
            {
                // there are more than 1 loss in the sequence
                // move the node to the next and update the starter as the next loss inSeqNo(seqno)

                // find next node
                int j = (loc + 1) % m_iSize;

                // remove the "seqno" and change the starter as next seq. no.
                m_piData1[j] = UdtSequenceNumber.incseq(m_piData1[loc]);

                // process the sequence end
                if (UdtSequenceNumber.seqcmp(m_piData2[loc], UdtSequenceNumber.incseq(m_piData1[loc])) > 0)
                    m_piData2[j] = m_piData2[loc];

                // remove the current node
                m_piData1[loc] = -1;
                m_piData2[loc] = -1;

                // update list pointer
                m_piNext[j] = m_piNext[loc];
                m_piPrior[j] = m_piPrior[loc];

                if (m_iHead == loc)
                    m_iHead = j;
                else
                    m_piNext[m_piPrior[j]] = j;

                if (m_iTail == loc)
                    m_iTail = j;
                else
                    m_piPrior[m_piNext[j]] = j;
            }

            m_iLength--;

            return true;
        }

        // There is no loss sequence in the current position
        // the "seqno" may be contained in a previous node

        // searching previous node
        int i = (loc - 1 + m_iSize) % m_iSize;
        while (-1 == m_piData1[i])
            i = (i - 1 + m_iSize) % m_iSize;

        // not contained in this node, return
        if ((-1 == m_piData2[i]) || (UdtSequenceNumber.seqcmp(seqno, m_piData2[i]) > 0))
            return false;

        if (seqno == m_piData2[i])
        {
            // it is the sequence end

            if (seqno == UdtSequenceNumber.incseq(m_piData1[i]))
                m_piData2[i] = -1;
            else
                m_piData2[i] = UdtSequenceNumber.decseq(seqno);
        }
        else
        {
            // split the sequence

            // construct the second sequence from SequenceNumber.incseq(seqno) to the original sequence end
            // located at "loc + 1"
            loc = (loc + 1) % m_iSize;

            m_piData1[loc] = UdtSequenceNumber.incseq(seqno);
            if (UdtSequenceNumber.seqcmp(m_piData2[i], m_piData1[loc]) > 0)
                m_piData2[loc] = m_piData2[i];

            // the first (original) sequence is between the original sequence start to SequenceNumber.decseq(seqno)
            if (seqno == UdtSequenceNumber.incseq(m_piData1[i]))
                m_piData2[i] = -1;
            else
                m_piData2[i] = UdtSequenceNumber.decseq(seqno);

            // update the list pointer
            m_piNext[loc] = m_piNext[i];
            m_piNext[i] = loc;
            m_piPrior[loc] = i;

            if (m_iTail == i)
                m_iTail = loc;
            else
                m_piPrior[m_piNext[loc]] = loc;
        }

        m_iLength--;

        return true;
    }

    public bool remove(int seqno1, int seqno2)
    {
        if (seqno1 <= seqno2)
        {
            for (int i = seqno1; i <= seqno2; ++i)
                remove(i);
        }
        else
        {
            for (int j = seqno1; j < UdtSequenceNumber.m_iMaxSeqNo; ++j)
                remove(j);
            for (int k = 0; k <= seqno2; ++k)
                remove(k);
        }

        return true;
    }

    bool find(int seqno1, int seqno2)
    {
        if (0 == m_iLength)
            return false;

        int p = m_iHead;

        while (-1 != p)
        {
            if ((UdtSequenceNumber.seqcmp(m_piData1[p], seqno1) == 0) ||
                ((UdtSequenceNumber.seqcmp(m_piData1[p], seqno1) > 0) && (UdtSequenceNumber.seqcmp(m_piData1[p], seqno2) <= 0)) ||
                ((UdtSequenceNumber.seqcmp(m_piData1[p], seqno1) < 0) && (m_piData2[p] != -1) && UdtSequenceNumber.seqcmp(m_piData2[p], seqno1) >= 0))
                return true;

            p = m_piNext[p];
        }

        return false;
    }

    public int getLossLength()
    {
        return m_iLength;
    }

    public int getFirstLostSeq()
    {
        if (0 == m_iLength)
            return -1;

        return m_piData1[m_iHead];
    }

    public void getLossArray(int[] array, out int len, int limit)
    {
        len = 0;

        int i = m_iHead;

        while ((len < limit - 1) && (-1 != i))
        {
            array[len] = m_piData1[i];
            if (-1 != m_piData2[i])
            {
                // there are more than 1 loss in the sequence
                array[len] = (int)((uint)array[len] | 0x80000000);
                ++len;
                array[len] = m_piData2[i];
            }

            ++len;

            i = m_piNext[i];
        }
    }
}