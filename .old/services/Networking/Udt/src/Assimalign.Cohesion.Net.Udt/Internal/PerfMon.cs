using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;
internal class PerfMon
{
    // global measurements
    internal long msTimeStamp;                    // time since the UDT entity is started, in milliseconds
    internal long pktSentTotal;                   // total number of sent data packets, including retransmissions
    internal long pktRecvTotal;                   // total number of received packets
    internal int pktSndLossTotal;                 // total number of lost packets (sender side)
    internal int pktRcvLossTotal;                 // total number of lost packets (receiver side)
    internal int pktRetransTotal;                 // total number of retransmitted packets
    internal int pktSentACKTotal;                 // total number of sent ACK packets
    internal int pktRecvACKTotal;                 // total number of received ACK packets
    internal int pktSentNAKTotal;                 // total number of sent NAK packets
    internal int pktRecvNAKTotal;                 // total number of received NAK packets
    internal long usSndDurationTotal;             // total time duration when UDT is sending data (idle time exclusive)

    // local measurements
    internal long pktSent;                        // number of sent data packets, including retransmissions
    internal long pktRecv;                        // number of received packets
    internal int pktSndLoss;                      // number of lost packets (sender side)
    internal int pktRcvLoss;                      // number of lost packets (receiver side)
    internal int pktRetrans;                      // number of retransmitted packets
    internal int pktSentACK;                      // number of sent ACK packets
    internal int pktRecvACK;                      // number of received ACK packets
    internal int pktSentNAK;                      // number of sent NAK packets
    internal int pktRecvNAK;                      // number of received NAK packets
    internal double mbpsSendRate;                 // sending rate in Mb/s
    internal double mbpsRecvRate;                 // receiving rate in Mb/s
    internal long usSndDuration;                  // busy sending time (i.e., idle time exclusive)

    // instant measurements
    internal double usPktSndPeriod;               // packet sending period, in microseconds
    internal int pktFlowWindow;                   // flow window size, in number of packets
    internal int pktCongestionWindow;             // congestion window size, in number of packets
    internal int pktFlightSize;                   // number of packets on flight
    internal double msRTT;                        // RTT, in milliseconds
    internal double mbpsBandwidth;                // estimated bandwidth, in Mb/s
    internal int byteAvailSndBuf;                 // available UDT sender buffer size
    internal int byteAvailRcvBuf;                 // available UDT receiver buffer size
};