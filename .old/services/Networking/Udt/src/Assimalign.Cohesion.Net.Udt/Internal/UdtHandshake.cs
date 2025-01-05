using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal class UdtHandshake
{
    public const int ContentSize = 48;    // Size of hand shake data

    

    public UdtHandshake()
    {
        for (int i = 0; i < 4; ++i)
        {
            m_piPeerIP[i] = 0;

        }
    }

    /// <summary>
    /// UDT version
    /// </summary>
    public int Version { get; set; }
    /// <summary>
    /// UDT socket type
    /// </summary>
    public SocketType SocketType { get; set; }
    /// <summary>
    /// random initial sequence number 
    /// </summary>
    public int InitialSequenceNumber { get; set; }
    /// <summary>
    /// maximum segment size
    /// </summary>
    public int MaximumSegmentSize { get; set; }
    /// <summary>
    /// flow control window size
    /// </summary>
    public int FlowControlWindowSize { get; set; }
    /// <summary>
    /// connection request type: 1: regular connection request, 0: rendezvous connection request, -1/-2: response
    /// </summary>
    public int RequestType { get; set; }
    /// <summary>
    /// Socket Id
    /// </summary>
    public int SocketId { get; set; }
    /// <summary>
    /// Cookie
    /// </summary>
    public int Cookie { get; set; }

    public uint[] m_piPeerIP = new uint[4];    // The IP address that the peer's UDP port is bound to

    public unsafe void Serialize(byte[] buffer)
    {
        fixed (byte* pb = buffer)
        {
            int* p = (int*)(pb);
            *p++ = Version;
            *p++ = (int)SocketType;
            *p++ = InitialSequenceNumber;
            *p++ = MaximumSegmentSize;
            *p++ = FlowControlWindowSize;
            *p++ = RequestType;
            *p++ = SocketId;
            *p++ = Cookie;
            for (int i = 0; i < 4; ++i)
            {
                *p++ = (int)m_piPeerIP[i];
            }
        }
    }
    public unsafe bool Deserialize(byte[] buffer, int size)
    {
        if (size < ContentSize)
        {
            return false;

        }

        fixed (byte* pb = buffer)
        {
            int* p = (int*)(pb);
            Version = *p++;
            SocketType = (SocketType)(*p++);
            InitialSequenceNumber = *p++;
            MaximumSegmentSize = *p++;
            FlowControlWindowSize = *p++;
            RequestType = *p++;
            SocketId = *p++;
            Cookie = *p++;
            for (int i = 0; i < 4; ++i)
            {
                m_piPeerIP[i] = (uint)*p++;

            }
        }

        return true;
    }
    public override string ToString()
    {
        string type = "connection request";
        if (RequestType == 0)
        {
            type = "rendezvouz";

        }
        if (RequestType < 0)
        {
            type = "reponse";

        }
        if (RequestType == 1002)
        {
            type = "rejected request";

        }
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("  Version      " + Version);
        sb.AppendLine("  Type         " + type);
        sb.AppendLine("  Cookie       " + Cookie);
        //sb.AppendLine("  Socket type  " + m_iType.ToString());
        //sb.AppendLine("  Socket id    " + m_iID);
        sb.AppendLine("  Initial seq# " + InitialSequenceNumber);
        //sb.AppendLine("  MSS          " + m_iMSS);
        //sb.AppendLine("  Flight size  " + m_iFlightFlagSize);

        return sb.ToString();
    }
}
