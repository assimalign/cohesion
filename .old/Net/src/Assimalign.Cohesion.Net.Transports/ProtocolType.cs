// Ignore Spelling: Icmp, Igmp, Ggp, Idp, Ipv, Ipx, Ipx, Spx, SpxII, Quic

namespace Assimalign.Cohesion.Net.Transports;

public enum ProtocolType
{
    /// <summary>
    /// 
    /// </summary>
    Unspecified = -1,
    /// <summary>
    /// 
    /// </summary>
    Icmp,
    /// <summary>
    /// 
    /// </summary>
    IcmpV6,
    /// <summary>
    /// 
    /// </summary>
    Igmp,
    /// <summary>
    /// 
    /// </summary>
    Ggp,
    /// <summary>
    /// 
    /// </summary>
    IPv6,
    /// <summary>
    /// 
    /// </summary>
    IPv4,
    /// <summary>
    /// 
    /// </summary>
    Tcp,
    /// <summary>
    /// 
    /// </summary>
    Pup,
    /// <summary>
    /// 
    /// </summary>
    Udp,
    /// <summary>
    /// 
    /// </summary>
    Idp,
    /// <summary>
    ///  Net Disk ProtocolType (unofficial).
    /// </summary>
    ND,
    /// <summary>
    /// Raw IP packet protocol.
    /// </summary>
    Raw,
    /// <summary>
    /// Internet Packet Exchange ProtocolType.
    /// </summary>
    Ipx,
    /// <summary>
    /// Sequenced Packet Exchange protocol.  
    /// </summary>
    Spx,
    /// <summary>
    /// Sequenced Packet Exchange version 2 protocol
    /// </summary>
    SpxII,
    /// <summary>
    /// 
    /// </summary>
    Quic
}
