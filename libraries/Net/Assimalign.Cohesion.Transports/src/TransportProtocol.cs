using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Transports;

public partial struct TransportProtocol
{
    public static TransportProtocol Icmp => "Icmp";
    public static TransportProtocol IcmpV6 => "IcmpV6";
    public static TransportProtocol Igmp => "Igmp";
    public static TransportProtocol Ggp => "Ggp";
    public static TransportProtocol Idp => "Idp";
    public static TransportProtocol Ipv => "Ipv";
    public static TransportProtocol Ipx => "Ipx";
    public static TransportProtocol Spx => "Spx";
    public static TransportProtocol SpxII => "SpxII";
    public static TransportProtocol Unspecified => "Unspecified";
    public static TransportProtocol IPv6 => "IPv6";
    public static TransportProtocol IPv4 => "IPv4";
    public static TransportProtocol Tcp => "Tcp";
    public static TransportProtocol Pup => "Pup";
    public static TransportProtocol Udp => "Udp";
    public static TransportProtocol ND => "ND";
    public static TransportProtocol Raw => "Raw";
    public static TransportProtocol Quic => "Quic";
    public static TransportProtocol Http => "Http";
    public static TransportProtocol Amqp => "Amqp";
}
