// UDT ACK Sub-sequence Number: 0 - (2^31 - 1)

namespace Assimalign.Cohesion.Net.Udt.Internal;

internal static class UdtAcknowledgementNumber
{
    public static int incack(int acknowledgemen)
    {
        return (acknowledgemen == m_iMaxAckSeqNo) ? 0 : acknowledgemen + 1;
    }

    public static int m_iMaxAckSeqNo = 0x7FFFFFFF;         // maximum ACK sub-sequence number used in UDT
}