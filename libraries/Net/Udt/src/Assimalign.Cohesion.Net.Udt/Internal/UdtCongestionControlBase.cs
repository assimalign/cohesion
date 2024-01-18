using System;
using UDTSOCKET = System.Int32;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class UdtCongestionControlBase
{
    protected const int m_iSYNInterval = UdtCongestionControl.m_iSYNInterval;	// UDT constant parameter, SYN

    public double m_dPktSndPeriod;              // Packet sending period, in microseconds
    public double m_dCWndSize;                  // Congestion window size, in packets

    protected int m_iBandwidth;           // estimated bandwidth, packets per second
    protected double m_dMaxCWndSize;               // maximum cwnd size, in packets

    protected int m_iMSS;             // Maximum Packet Size, including all packet headers
    protected int m_iSndCurrSeqNo;        // current maximum seq no sent out
    protected int m_iRcvRate;         // packet arrive rate at receiver side, packets per second
    protected int m_iRTT;             // current estimated RTT, microsecond

    protected string m_pcParam;            // user defined parameter

    public UDTSOCKET m_UDT;                     // The UDT entity that this congestion control algorithm is bound to

    public int m_iACKPeriod;                    // Periodical timer to send an ACK, in milliseconds
    public int m_iACKInterval;                  // How many packets to send one ACK, in packets

    public bool m_bUserDefinedRTO;              // if the RTO value is defined by users
    public int m_iRTO;                          // RTO value, microseconds

    PerfMon m_PerfInfo = new PerfMon();                 // protocol statistics information

    public UdtCongestionControlBase()
    {
        m_dPktSndPeriod = 1.0;
        m_dCWndSize = 16.0;
        m_pcParam = null;
        m_iACKPeriod = 0;
        m_iACKInterval = 0;
        m_bUserDefinedRTO = false;
        m_iRTO = -1;
    }

    // Functionality:
    //    Callback function to be called (only) at the start of a UDT connection.
    //    note that this is different from CCC(), which is always called.
    // Parameters:
    //    None.
    // Returned value:
    //    None.

    public virtual void Initialize() { }

    // Functionality:
    //    Callback function to be called when a UDT connection is closed.
    // Parameters:
    //    None.
    // Returned value:
    //    None.

    public virtual void Close() { }

    // Functionality:
    //    Callback function to be called when an ACK packet is received.
    // Parameters:
    //    0) [in] ackno: the data sequence number acknowledged by this ACK.
    // Returned value:
    //    None.

    public virtual void OnAcknowledgement(int seqno) { }

    // Functionality:
    //    Callback function to be called when a loss report is received.
    // Parameters:
    //    0) [in] losslist: list of sequence number of packets, in the format describled in packet.cpp.
    //    1) [in] size: length of the loss list.
    // Returned value:
    //    None.

    public virtual void OnLoss(int[] loss, int length) { }

    // Functionality:
    //    Callback function to be called when a timeout event occurs.
    // Parameters:
    //    None.
    // Returned value:
    //    None.

    public virtual void OnTimeout() { }

    // Functionality:
    //    Callback function to be called when a data is sent.
    // Parameters:
    //    0) [in] seqno: the data sequence number.
    //    1) [in] size: the payload size.
    // Returned value:
    //    None.

    public virtual void OnPacketSent(UdtPacket packet) { }

    // Functionality:
    //    Callback function to be called when a data is received.
    // Parameters:
    //    0) [in] seqno: the data sequence number.
    //    1) [in] size: the payload size.
    // Returned value:
    //    None.

    public virtual void OnPacketReceived(UdtPacket packet) { }

    // Functionality:
    //    Callback function to Process a user defined packet.
    // Parameters:
    //    0) [in] pkt: the user defined packet.
    // Returned value:
    //    None.

    public virtual void processCustomMsg(UdtPacket packet) { }


    protected void setACKTimer(int msINT)
    {
        m_iACKPeriod = msINT > m_iSYNInterval ? m_iSYNInterval : msINT;
    }

    protected void setACKInterval(int pktINT)
    {
        m_iACKInterval = pktINT;
    }

    protected void setRTO(int usRTO)
    {
        m_bUserDefinedRTO = true;
        m_iRTO = usRTO;
    }

    protected void sendCustomMsg(UdtPacket pkt)
    {
        UdtCongestionControl u = UdtCongestionControl.s_UDTUnited.lookup(m_UDT);

        if (null != u)
        {
            pkt.SetId(u.m_PeerID);
            u.m_pSndQueue.sendto(u.m_pPeerAddr, pkt);
        }
    }

    protected PerfMon getPerfInfo()
    {
        try
        {
            UdtCongestionControl u = UdtCongestionControl.s_UDTUnited.lookup(m_UDT);
            if (null != u)
                u.sample(m_PerfInfo, false);
        }
        catch (Exception e)
        {
            return null;
        }

        return m_PerfInfo;
    }

    public void setMSS(int mss)
    {
        m_iMSS = mss;
    }

    public void setBandwidth(int bw)
    {
        m_iBandwidth = bw;
    }

    public void setSndCurrSeqNo(int seqno)
    {
        m_iSndCurrSeqNo = seqno;
    }

    public void setRcvRate(int rcvrate)
    {
        m_iRcvRate = rcvrate;
    }

    public void SetMaximumCongestionWindowSize(int cwnd)
    {
        m_dMaxCWndSize = cwnd;
    }

    public void setRTT(int rtt)
    {
        m_iRTT = rtt;
    }

    protected void SetUserParameter(string param)
    {
        m_pcParam = param;
    }
}

