using System;
using System.Net;

namespace Assimalign.Cohesion.Net.Dns;

public class DnsARecord : DnsRecord
{
    public DnsARecord(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address, nameof(address));

        Address = address;
    }

    /// <summary>
    /// 
    /// </summary>
    public IPAddress Address { get; }
    /// <summary>
    /// 
    /// </summary>
    public override DnsRecordKind Kind => DnsRecordKind.A;
}
