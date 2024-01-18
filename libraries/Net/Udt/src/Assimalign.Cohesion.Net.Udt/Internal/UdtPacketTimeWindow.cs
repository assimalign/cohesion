using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class UdtPacketTimeWindow
{
    public UdtPacketTimeWindow(int asize = 16, int psize = 16)
    {
        m_iAWSize = asize;
        m_iPWSize = psize;
        m_iMinPktSndInt = 1000000;
        m_piPktWindow = new int[m_iAWSize];
        m_piPktReplica = new int[m_iAWSize];
        m_piProbeWindow = new int[m_iPWSize];
        m_piProbeReplica = new int[m_iPWSize];

        m_LastArrTime = UdtTimer.getTime();

        for (int i = 0; i < m_iAWSize; ++i)
            m_piPktWindow[i] = 1000000;

        for (int k = 0; k < m_iPWSize; ++k)
            m_piProbeWindow[k] = 1000;
    }

    // Functionality:
    //    read the minimum packet sending interval.
    // Parameters:
    //    None.
    // Returned value:
    //    minimum packet sending interval (microseconds).

    public int getMinPktSndInt()
    {
        return m_iMinPktSndInt;
    }

    // Functionality:
    //    Calculate the packes arrival speed.
    // Parameters:
    //    None.
    // Returned value:
    //    Packet arrival speed (packets per second).

    public int getPktRcvSpeed()
    {
        // get median value, but cannot change the original value order in the window
        Array.Copy(m_piPktWindow, m_piPktReplica, m_iAWSize - 1); // why -1 ???
        Array.Sort(m_piPktReplica); // need -1 here ???
        int median = m_piPktReplica[m_iAWSize / 2];

        int count = 0;
        int sumMicrosecond = 0;
        int upper = median << 3;
        int lower = median >> 3;

        // median filtering
        for (int i = 0, n = m_iAWSize; i < n; ++i)
        {
            if ((m_piPktWindow[i] < upper) && (m_piPktWindow[i] > lower))
            {
                ++count;
                sumMicrosecond += m_piPktWindow[i];
            }
        }
        double packetsPerMicrosecond = (double)count / sumMicrosecond;

        // claculate speed, or return 0 if not enough valid value
        if (count > (m_iAWSize >> 1))
            return (int)Math.Ceiling(1000000 * packetsPerMicrosecond);
        else
            return 0;
    }

    // Functionality:
    //    Estimate the bandwidth.
    // Parameters:
    //    None.
    // Returned value:
    //    Estimated bandwidth (packets per second).

    public int getBandwidth()
    {
        // get median value, but cannot change the original value order in the window
        Array.Copy(m_piProbeWindow, m_piProbeReplica, m_iPWSize - 1); // why -1 ???
        Array.Sort(m_piProbeReplica); // need -1 here ???
        int median = m_piProbeReplica[m_iPWSize / 2];

        int count = 1;
        int sum = median;
        int upper = median << 3;
        int lower = median >> 3;

        // median filtering
        for (int i = 0, n = m_iPWSize; i < n; ++i)
        {
            if ((m_piProbeWindow[i] < upper) && (m_piProbeWindow[i] > lower))
            {
                ++count;
                sum += m_piProbeWindow[i];
            }
        }

        return (int)Math.Ceiling(1000000.0 / ((double)sum / (double)count));
    }

    // Functionality:
    //    Record time information of a packet sending.
    // Parameters:
    //    0) currtime: timestamp of the packet sending.
    // Returned value:
    //    None.

    public void onPktSent(int currtime)
    {
        int interval = currtime - m_iLastSentTime;

        if ((interval < m_iMinPktSndInt) && (interval > 0))
            m_iMinPktSndInt = interval;

        m_iLastSentTime = currtime;
    }

    // Functionality:
    //    Record time information of an arrived packet.
    // Parameters:
    //    None.
    // Returned value:
    //    None.

    public void onPktArrival()
    {
        m_CurrArrTime = UdtTimer.getTime();

        // record the packet interval between the current and the last one
        m_piPktWindow[m_iPktWindowPtr] = (int)(m_CurrArrTime - m_LastArrTime);

        // the window is logically circular
        ++m_iPktWindowPtr;
        if (m_iPktWindowPtr == m_iAWSize)
            m_iPktWindowPtr = 0;

        // remember last packet arrival time
        m_LastArrTime = m_CurrArrTime;
    }

    // Functionality:
    //    Record the arrival time of the first probing packet.
    // Parameters:
    //    None.
    // Returned value:
    //    None.

    public void probe1Arrival()
    {
        m_ProbeTime = UdtTimer.getTime();
    }

    // Functionality:
    //    Record the arrival time of the second probing packet and the interval between packet pairs.
    // Parameters:
    //    None.
    // Returned value:
    //    None.

    public void probe2Arrival()
    {
        m_CurrArrTime = UdtTimer.getTime();

        // record the probing packets interval
        m_piProbeWindow[m_iProbeWindowPtr] = (int)(m_CurrArrTime - m_ProbeTime);
        // the window is logically circular
        ++m_iProbeWindowPtr;
        if (m_iProbeWindowPtr == m_iPWSize)
            m_iProbeWindowPtr = 0;
    }

    int m_iAWSize;               // size of the packet arrival history window
    int[] m_piPktWindow;          // packet information window
    int[] m_piPktReplica;
    int m_iPktWindowPtr;         // position pointer of the packet info. window.

    int m_iPWSize;               // size of probe history window size
    int[] m_piProbeWindow;        // record inter-packet time for probing packet pairs
    int[] m_piProbeReplica;
    int m_iProbeWindowPtr;       // position pointer to the probing window

    int m_iLastSentTime;         // last packet sending time
    int m_iMinPktSndInt;         // Minimum packet sending interval

    ulong m_LastArrTime;      // last packet arrival time
    ulong m_CurrArrTime;      // current packet arrival time
    ulong m_ProbeTime;        // arrival time of the first probing packet
}
