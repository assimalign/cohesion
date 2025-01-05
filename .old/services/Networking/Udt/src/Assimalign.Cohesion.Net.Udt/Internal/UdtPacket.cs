/*
    0                   1                   2                   3
    0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |                        Packet Header                          |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |                                                               |
   ~              Data / Control Information Field                 ~
   |                                                               |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

    0                   1                   2                   3
    0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |0|                        Sequence Number                      |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |ff |o|                     Message Number                      |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |                          Time Stamp                           |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |                     Destination Socket ID                     |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

   bit 0:
      0: Data Packet
      1: Control Packet
   bit ff:
      11: solo message packet
      10: first packet of a message
      01: last packet of a message
   bit o:
      0: in order delivery not required
      1: in order delivery required

    0                   1                   2                   3
    0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |1|            Type             |             Reserved          |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |                       Additional Info                         |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |                          Time Stamp                           |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |                     Destination Socket ID                     |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

   bit 1-15:
      0: Protocol Connection Handshake
              Add. Info:    Undefined
              Control Info: Handshake information (see CHandShake)
      1: Keep-alive
              Add. Info:    Undefined
              Control Info: None
      2: Acknowledgement (ACK)
              Add. Info:    The ACK sequence number
              Control Info: The sequence number to which (but not include) all the previous packets have beed received
              Optional:     RTT
                            RTT Variance
                            available receiver buffer size (in bytes)
                            advertised flow window size (number of packets)
                            estimated bandwidth (number of packets per second)
      3: Negative Acknowledgement (NAK)
              Add. Info:    Undefined
              Control Info: Loss list (see loss list coding below)
      4: Congestion/Delay Warning
              Add. Info:    Undefined
              Control Info: None
      5: Shutdown
              Add. Info:    Undefined
              Control Info: None
      6: Acknowledgement of Acknowledement (ACK-square)
              Add. Info:    The ACK sequence number
              Control Info: None
      7: Message Drop Request
              Add. Info:    Message ID
              Control Info: first sequence number of the message
                            last seqeunce number of the message
      8: Error Signal from the Peer Side
              Add. Info:    Error code
              Control Info: None
      0x7FFF: Explained by bits 16 - 31
              
   bit 16 - 31:
      This space is used for future expansion or user defined control packets. 

    0                   1                   2                   3
    0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |1|                 Sequence Number a (first)                   |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |0|                 Sequence Number b (last)                    |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
   |0|                 Sequence Number (single)                    |
   +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+

   Loss List Field Coding:
      For any consectutive lost seqeunce numbers that the differnece between
      the last and first is more than 1, only record the first (a) and the
      the last (b) sequence numbers in the loss list field, and modify the
      the first bit of a to 1.
      For any single loss or consectutive loss less than 2 packets, use
      the original sequence numbers in the field.
*/
using System;
using System.Net;
using System.Text;

namespace Assimalign.Cohesion.Net.Udt.Internal;


internal unsafe class UdtPacket
{
    private enum ControlType
    {
        Handshake = 0,
        KeepAlive = 1,
        Ack = 2,
        Nak = 3,
        CongestionWarning = 4,
        Shutdown = 5,
        Ack2 = 6,
        DropMessage = 7,
        Error = 8,
        UserType = 32767,
    }

    private const int sequenceNumberIndex = 0;                      // alias: sequence number
    private const int messageNumberIndex = 1;                       // alias: message number
    private const int timestampIndex = 2;                           // alias: timestamp
    private const int idIndex = 3;                                  // alias: socket ID

    public const int packetHeaderSize = 16;    // packet header size

    private readonly UdtIOVector[] m_PacketVector = new UdtIOVector[2];             // The 2-demension vector of UDT packet [header, data]

    public override string ToString()
    {
        StringBuilder stringBuilder = new StringBuilder();

        if (getFlag() == 0)
        {
            byte[] data = GetDataBytes();
            stringBuilder.AppendFormat("Data length {0} bytes", data != null ? data.Length : 0);
            stringBuilder.AppendLine();
            stringBuilder.AppendLine("  SeqNo     " + GetSequenceNumber());
            stringBuilder.AppendLine("  MsgNo     " + GetMessageNumber());
            stringBuilder.AppendLine("  Timestamp " + GetTimestamp());
            stringBuilder.AppendLine("  SocketID  " + GetId());
            stringBuilder.Append("  Data: {");
            if (data != null)
            {
                for (int i = 0; i < Math.Min(data.Length, 10); ++i)
                {
                    stringBuilder.Append(data[i] + ",");
                }
                stringBuilder.Length = stringBuilder.Length - 1;
            }
            stringBuilder.AppendLine("}");
        }
        else if (getFlag() == 1)
        {
            int type = getType();
            stringBuilder.AppendFormat("CTRL {0} ({1})", (UdtPacket.ControlType)type, type);
            stringBuilder.AppendLine();
            switch (type)
            {
                case 2: //0010 - Acknowledgement (ACK)
                    stringBuilder.AppendFormat("  Ack sequence {0}", getAckSeqNo());
                    stringBuilder.AppendLine();
                    break;

                case 6: //0110 - Acknowledgement of Acknowledgement (ACK-2)
                    stringBuilder.AppendFormat("  Ack2 sequence {0}", getAckSeqNo());
                    stringBuilder.AppendLine();
                    break;

                case 3: //0011 - Loss Report (NAK)
                    break;

                case 4: //0100 - Congestion Warning
                    break;

                case 1: //0001 - Keep-alive
                    break;

                case 0: //0000 - Handshake
                    // control info filed is handshake info
                    UdtHandshake handshake = new UdtHandshake();
                    handshake.Deserialize(GetDataBytes(), UdtHandshake.ContentSize);
                    stringBuilder.AppendFormat(handshake.ToString());
                    stringBuilder.AppendLine();
                    break;

                case 5: //0101 - Shutdown
                    break;

                case 7: //0111 - Message Drop Request

                    break;

                case 8: //1000 - Error Signal from the Peer Side
                        // Error type
                     stringBuilder.AppendLine("Error: " + m_PacketVector[0].iov_base[messageNumberIndex].ToString());

                    break;

                case 32767: //0x7FFF - Reserved for user defined control packets
                    break;

                default:
                    break;
            }
        }

        return stringBuilder.ToString();
    }

    public UdtPacket()
    {
        m_PacketVector[0].iov_base = new uint[4];
        m_PacketVector[0].iov_len = packetHeaderSize;
        m_PacketVector[1].iov_base = null;
        m_PacketVector[1].iov_len = 0;
    }

    ~UdtPacket()
    {
    }

    public void Clone(UdtPacket source)
    {
        Buffer.BlockCopy(source.m_PacketVector[0].iov_base, 0, m_PacketVector[0].iov_base, 0, packetHeaderSize);

        if (source.m_PacketVector[1].iov_base == null)
        {
            m_PacketVector[1].iov_base = null;
            m_PacketVector[1].iov_len = source.m_PacketVector[1].iov_len;
            return;
        }

        m_PacketVector[1].iov_base = new uint[source.m_PacketVector[1].iov_base.Length];
        Buffer.BlockCopy(source.m_PacketVector[1].iov_base, 0, m_PacketVector[1].iov_base, 0, source.m_PacketVector[1].iov_len);
        m_PacketVector[1].iov_len = source.m_PacketVector[1].iov_len;
    }

    public int GetSequenceNumber()
    {
        return (int)m_PacketVector[0].iov_base[sequenceNumberIndex];
    }

    public void SetSequenceNumber(int sequenceNumber)
    {
        m_PacketVector[0].iov_base[sequenceNumberIndex] = (uint)sequenceNumber;
    }

    public uint GetMessageNumber()
    {
        return m_PacketVector[0].iov_base[messageNumberIndex];
    }

    public void SetMessageNumber(uint messageNumber)
    {
        m_PacketVector[0].iov_base[messageNumberIndex] = messageNumber;
    }

    public int GetTimestamp()
    {
        return (int)m_PacketVector[0].iov_base[timestampIndex];
    }

    public void SetTimestamp(int timestamp)
    {
        m_PacketVector[0].iov_base[timestampIndex] = (uint)timestamp;
    }

    public int GetId()
    {
        return (int)m_PacketVector[0].iov_base[idIndex];
    }

    public void SetId(int id)
    {
        m_PacketVector[0].iov_base[idIndex] = (uint)id;
    }

    public byte[] GetBytes()
    {
        int dataLength = m_PacketVector[1].iov_len;

        byte[] bytes = new byte[packetHeaderSize + dataLength];
        Buffer.BlockCopy(m_PacketVector[0].iov_base, 0, bytes, 0, packetHeaderSize);

        if (dataLength == 0 || m_PacketVector[1].iov_base == null)
        {
            return bytes;
        }

        Buffer.BlockCopy(m_PacketVector[1].iov_base, 0, bytes, packetHeaderSize, dataLength);
        return bytes;
    }

    public byte[] GetHeaderBytes()
    {
        byte[] bytes = new byte[packetHeaderSize];
        Buffer.BlockCopy(m_PacketVector[0].iov_base, 0, bytes, 0, packetHeaderSize);
        return bytes;
    }

    public int GetDataBytes(int packetOffset, byte[] data, int dataOffset, int length)
    {
        if (m_PacketVector[1].iov_base == null)
        {
            return 0;
        }

        int bufferAvailable = data.Length - dataOffset;
        if (bufferAvailable < length)
        {
            length = bufferAvailable;
        }

        Buffer.BlockCopy(m_PacketVector[1].iov_base, packetOffset, data, dataOffset, length);
        return length;
    }

    public int GetIntFromData(int offset)
    {
        return (int)m_PacketVector[1].iov_base[offset];
    }

    public byte[] GetDataBytes()
    {
        if (m_PacketVector[1].iov_base == null)
        {
            return null;
        }

        int dataLength = m_PacketVector[1].iov_len;
        if (dataLength <= 0)
        {
            return null;

        }

        byte[] bytes = new byte[dataLength];
        Buffer.BlockCopy(m_PacketVector[1].iov_base, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public bool SetHeaderAndDataFromBytes(byte[] bytes, int length)
    {
        if (length < packetHeaderSize)
        {
            return false;

        }

        Buffer.BlockCopy(bytes, 0, m_PacketVector[0].iov_base, 0, packetHeaderSize);

        int dataLength = length - packetHeaderSize;
        if (dataLength == 0)
        {
            m_PacketVector[1].iov_base = null;
            m_PacketVector[1].iov_len = 0;
            return true;
        }

        SetDataFromBytes(bytes, packetHeaderSize, dataLength);

        return true;
    }

    public void SetDataFromBytes(byte[] bytes)
    {
        SetDataFromBytes(bytes, 0, bytes.Length);
    }

    public void SetDataFromBytes(byte[] bytes, int offset, int byteCount)
    {
        int intCount = byteCount / 4;
        if (byteCount % 4 != 0)
        {
            ++intCount;

        }

        m_PacketVector[1].iov_base = new uint[intCount];
        m_PacketVector[1].iov_len = byteCount;

        Buffer.BlockCopy(bytes, offset, m_PacketVector[1].iov_base, 0, byteCount);
    }

    public void ConvertControlInfoToNetworkOrder()
    {
        if (getFlag() == 0)
        {
            return;

        }

        if (m_PacketVector[1].iov_base == null || m_PacketVector[1].iov_len == 0)
        {
            return;

        }

        for (int i = 0; i < m_PacketVector[1].iov_base.Length; ++i)
        {
            m_PacketVector[1].iov_base[i] =
                (uint)IPAddress.HostToNetworkOrder((int)m_PacketVector[1].iov_base[i]);
        }
    }

    public void ConvertControlInfoToHostOrder()
    {
        if (getFlag() == 0)
        {
            return;

        }

        if (m_PacketVector[1].iov_base == null || m_PacketVector[1].iov_len == 0)
        {
            return;

        }

        for (int i = 0; i < m_PacketVector[1].iov_base.Length; ++i)
        {
            m_PacketVector[1].iov_base[i] =
                (uint)IPAddress.NetworkToHostOrder((int)m_PacketVector[1].iov_base[i]);
        }
    }

    public void ConvertHeaderToNetworkOrder()
    {
        for (int i = 0; i < m_PacketVector[0].iov_base.Length; ++i)
        {
            m_PacketVector[0].iov_base[i] =
                (uint)IPAddress.HostToNetworkOrder((int)m_PacketVector[0].iov_base[i]);
        }
    }

    public void ConvertHeaderToHostOrder()
    {
        for (int i = 0; i < m_PacketVector[0].iov_base.Length; ++i)
        {
            m_PacketVector[0].iov_base[i] =
                (uint)IPAddress.NetworkToHostOrder((int)m_PacketVector[0].iov_base[i]);
        }
    }

    public int getLength()
    {
        return m_PacketVector[1].iov_len;
    }

    public void setLength(int len)
    {
        m_PacketVector[1].iov_len = len;
    }

    static UdtIOVector MakeIovec(void* rparam, int size)
    {
        UdtIOVector result = new UdtIOVector();
        result.iov_len = size;

        if (rparam == null)
        {
            return result;

        }

        result.iov_base = new uint[size >> 2];

        uint* pIn = (uint*)rparam;
        for (int i = 0; i < size >> 2; ++i)
        {
            result.iov_base[i] = *pIn++;
        }

        return result;
    }

    public void pack(UdtHandshake hs)
    {
        // TODO avoid this inefficient buffer creation
        // we copy this buffer into another buffer later
        byte[] bytes = new byte[UdtHandshake.ContentSize];
        hs.Serialize(bytes);
        pack(0, bytes);
    }

    public void pack(int pkttype, void* lparam)
    {
        if (pkttype != 6 && pkttype != 8)
        {
            throw new Exception("pkttype must be 6 or 8");

        }

        pack(pkttype, lparam, (void*)null, 0);
    }

    public void pack(int pkttype, byte[] rparam)
    {
        if (pkttype != 0)
        {
            throw new Exception("pkttype must be 0");

        }

        fixed (byte* prparam = rparam)
        {
            pack(pkttype, (void*)null, (void*)prparam, rparam.Length);
        }
    }

    public void pack(int pkttype, int lparam, int[] rparam)
    {
        if (pkttype != 2)
        {
            throw new Exception("pkttype must be 2");

        }

        fixed (int* prparam = rparam)
        {
            pack(pkttype, &lparam, (void*)prparam, rparam.Length * 4);
        }
    }

    public void pack(int pkttype, int lparam, int[] rparam, int length)
    {
        if (pkttype != 2)
        {
            throw new Exception("pkttype must be 2");

        }

        fixed (int* prparam = rparam)
        {
            pack(pkttype, &lparam, (void*)prparam, length * 4);
        }
    }

    public void pack(int pkttype, int[] rparam, int length)
    {
        if (pkttype != 3)
        {
            throw new Exception("pkttype must be 3");

        }

        fixed (int* prparam = rparam)
        {
            pack(pkttype, (void*)null, (void*)prparam, length * 4);
        }
    }

    public void pack(int pkttype)
    {
        if (pkttype != 1 && pkttype != 4 && pkttype != 5)
        {
            throw new Exception("pkttype must be 1, 4 or 5");

        }

        pack(pkttype, (void*)null, (void*)null, 0);
    }

    public void pack(int pkttype, void* lparam, void* rparam, int size)
    {
        // Set (bit-0 = 1) and (bit-1~15 = type)
        m_PacketVector[0].iov_base[sequenceNumberIndex] = (uint)0x80000000 | (uint)(pkttype << 16);

        // Set additional information and control information field
        switch (pkttype)
        {
            case 2: //0010 - Acknowledgement (ACK)
                    // ACK packet seq. no.
                if (null != lparam)
                {
                    m_PacketVector[0].iov_base[messageNumberIndex] = *(uint*)lparam;

                }

                // data ACK seq. no. 
                // optional: RTT (microsends), RTT variance (microseconds) advertised flow window size (packets), and estimated link capacity (packets per second)
                m_PacketVector[1] = MakeIovec(rparam, size);

                break;

            case 6: //0110 - Acknowledgement of Acknowledgement (ACK-2)
                    // ACK packet seq. no.
                m_PacketVector[0].iov_base[messageNumberIndex] = *(uint*)lparam;

                // control info field should be none
                // but "writev" does not allow this
                m_PacketVector[1] = MakeIovec(null, 4);

                break;

            case 3: //0011 - Loss Report (NAK)
                    // loss list
                m_PacketVector[1] = MakeIovec(rparam, size);

                break;

            case 4: //0100 - Congestion Warning
                    // control info field should be none
                    // but "writev" does not allow this
                m_PacketVector[1] = MakeIovec(null, 4);

                break;

            case 1: //0001 - Keep-alive
                    // control info field should be none
                    // but "writev" does not allow this
                m_PacketVector[1] = MakeIovec(null, 4);

                break;

            case 0: //0000 - Handshake
                    // control info filed is handshake info
                m_PacketVector[1] = MakeIovec(rparam, size);

                break;

            case 5: //0101 - Shutdown
                    // control info field should be none
                    // but "writev" does not allow this
                m_PacketVector[1] = MakeIovec(null, 4);

                break;

            case 7: //0111 - Message Drop Request
                    // msg id 
                m_PacketVector[0].iov_base[messageNumberIndex] = *(uint*)lparam;

                //first seq no, last seq no
                m_PacketVector[1] = MakeIovec(rparam, size);

                break;

            case 8: //1000 - Error Signal from the Peer Side
                    // Error type
                m_PacketVector[0].iov_base[messageNumberIndex] = *(uint*)lparam;

                // control info field should be none
                // but "writev" does not allow this
                m_PacketVector[1] = MakeIovec(null, 4);

                break;

            case 32767: //0x7FFF - Reserved for user defined control packets
                        // for extended control packet
                        // "lparam" contains the extended type information for bit 16 - 31
                        // "rparam" is the control information
                m_PacketVector[0].iov_base[sequenceNumberIndex] |= *(uint*)lparam;

                if (null != rparam)
                {
                    m_PacketVector[1] = MakeIovec(rparam, size);
                }
                else
                {
                    m_PacketVector[1] = MakeIovec(null, 4);
                }

                break;

            default:
                break;
        }
    }

    public UdtIOVector[] getPacketVector()
    {
        return m_PacketVector;
    }

    public int getFlag()
    {
        // read bit 0
        return (int)(m_PacketVector[0].iov_base[sequenceNumberIndex] >> 31);
    }

    public int getType()
    {
        // read bit 1~15
        return (int)((m_PacketVector[0].iov_base[sequenceNumberIndex] >> 16) & 0x00007FFF);
    }

    int getExtendedType()
    {
        // read bit 16~31
        return (int)(m_PacketVector[0].iov_base[sequenceNumberIndex] & 0x0000FFFF);
    }

    public int getAckSeqNo()
    {
        // read additional information field
        return (int)m_PacketVector[0].iov_base[messageNumberIndex];
    }

    public int getMsgBoundary()
    {
        // read [1] bit 0~1
        return (int)(m_PacketVector[0].iov_base[messageNumberIndex] >> 30);
    }

    public bool getMsgOrderFlag()
    {
        // read [1] bit 2
        return (1 == ((m_PacketVector[0].iov_base[messageNumberIndex] >> 29) & 1));
    }

    public int getMsgSeq()
    {
        // read [1] bit 3~31
        return (int)(m_PacketVector[0].iov_base[messageNumberIndex] & 0x1FFFFFFF);
    }
}


