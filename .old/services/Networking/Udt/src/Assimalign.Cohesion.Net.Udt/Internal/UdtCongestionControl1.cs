using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;


internal class UdtCongestionControl1 : UdtCongestionControlBase
{
    int m_iRCInterval;          // UDT Rate control interval
    ulong m_LastRCTime;      // last rate increase time
    bool m_bSlowStart;          // if in slow start phase
    int m_iLastAck;         // last ACKed seq no
    bool m_bLoss;           // if loss happened since last rate increase
    int m_iLastDecSeq;      // max pkt seq no sent out when last decrease happened
    double m_dLastDecPeriod;        // value of pktsndperiod when last decrease happened
    int m_iNAKCount;                     // NAK counter
    int m_iDecRandom;                    // random threshold on decrease by number of loss events
    int m_iAvgNAKNum;                    // average number of NAKs per congestion
    int m_iDecCount;            // number of decreases in a congestion epoch

    static Random m_random = new Random();


    public override void Initialize()
    {
        m_iRCInterval = m_iSYNInterval;
        m_LastRCTime = UdtTimer.getTime();
        setACKTimer(m_iRCInterval);

        m_bSlowStart = true;
        m_iLastAck = m_iSndCurrSeqNo;
        m_bLoss = false;
        m_iLastDecSeq = UdtSequenceNumber.decseq(m_iLastAck);
        m_dLastDecPeriod = 1;
        m_iAvgNAKNum = 0;
        m_iNAKCount = 0;
        m_iDecRandom = 1;

        m_dCWndSize = 16;
        m_dPktSndPeriod = 1;
    }

    public override void OnAcknowledgement(int ack)
    {
        long B = 0;
        double inc = 0;
        // Note: 1/24/2012
        // The minimum increase parameter is increased from "1.0 / m_iMSS" to 0.01
        // because the original was too small and caused sending rate to stay at low level
        // for long time.
        const double min_inc = 0.01;

        ulong currtime = UdtTimer.getTime();
        if (currtime - m_LastRCTime < (ulong)m_iRCInterval)
            return;

        m_LastRCTime = currtime;

        if (m_bSlowStart)
        {
            m_dCWndSize += UdtSequenceNumber.seqlen(m_iLastAck, ack);
            m_iLastAck = ack;

            if (m_dCWndSize > m_dMaxCWndSize)
            {
                m_bSlowStart = false;
                if (m_iRcvRate > 0)
                    m_dPktSndPeriod = 1000000.0 / m_iRcvRate;
                else
                    m_dPktSndPeriod = (m_iRTT + m_iRCInterval) / m_dCWndSize;
            }
        }
        else
            m_dCWndSize = m_iRcvRate / 1000000.0 * (m_iRTT + m_iRCInterval) + 16;

        // During Slow Start, no rate increase
        if (m_bSlowStart)
            return;

        if (m_bLoss)
        {
            m_bLoss = false;
            return;
        }

        B = (long)(m_iBandwidth - 1000000.0 / m_dPktSndPeriod);
        if ((m_dPktSndPeriod > m_dLastDecPeriod) && ((m_iBandwidth / 9) < B))
            B = m_iBandwidth / 9;
        if (B <= 0)
            inc = min_inc;
        else
        {
            // inc = max(10 ^ ceil(log10( B * MSS * 8 ) * Beta / MSS, 1/MSS)
            // Beta = 1.5 * 10^(-6)

            inc = Math.Pow(10.0, Math.Ceiling(Math.Log10(B * m_iMSS * 8.0))) * 0.0000015 / m_iMSS;

            if (inc < min_inc)
                inc = min_inc;
        }

        m_dPktSndPeriod = (m_dPktSndPeriod * m_iRCInterval) / (m_dPktSndPeriod * inc + m_iRCInterval);
    }

    public override void OnLoss(int[] losslist, int length)
    {
        //Slow Start stopped, if it hasn't yet
        if (m_bSlowStart)
        {
            m_bSlowStart = false;
            if (m_iRcvRate > 0)
            {
                // Set the sending rate to the receiving rate.
                m_dPktSndPeriod = 1000000.0 / m_iRcvRate;
                return;
            }
            // If no receiving rate is observed, we have to compute the sending
            // rate according to the current window size, and decrease it
            // using the method below.
            m_dPktSndPeriod = m_dCWndSize / (m_iRTT + m_iRCInterval);
        }

        m_bLoss = true;

        if (UdtSequenceNumber.seqcmp(losslist[0] & 0x7FFFFFFF, m_iLastDecSeq) > 0)
        {
            m_dLastDecPeriod = m_dPktSndPeriod;
            m_dPktSndPeriod = Math.Ceiling(m_dPktSndPeriod * 1.125);

            m_iAvgNAKNum = (int)Math.Ceiling(m_iAvgNAKNum * 0.875 + m_iNAKCount * 0.125);
            m_iNAKCount = 1;
            m_iDecCount = 1;

            m_iLastDecSeq = m_iSndCurrSeqNo;

            // remove global synchronization using randomization
            m_iDecRandom = (int)Math.Ceiling(m_iAvgNAKNum * m_random.NextDouble());
            if (m_iDecRandom < 1)
                m_iDecRandom = 1;
        }
        else if ((m_iDecCount++ < 5) && (0 == (++m_iNAKCount % m_iDecRandom)))
        {
            // 0.875^5 = 0.51, rate should not be decreased by more than half within a congestion period
            m_dPktSndPeriod = Math.Ceiling(m_dPktSndPeriod * 1.125);
            m_iLastDecSeq = m_iSndCurrSeqNo;
        }
    }

    public override void OnTimeout()
    {
        if (m_bSlowStart)
        {
            m_bSlowStart = false;
            if (m_iRcvRate > 0)
                m_dPktSndPeriod = 1000000.0 / m_iRcvRate;
            else
                m_dPktSndPeriod = m_dCWndSize / (m_iRTT + m_iRCInterval);
        }
        else
        {
            /*
            m_dLastDecPeriod = m_dPktSndPeriod;
            m_dPktSndPeriod = ceil(m_dPktSndPeriod * 2);
            m_iLastDecSeq = m_iLastAck;
            */
        }
    }
}