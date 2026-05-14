using System;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// Configuration for <see cref="StubDnsClient"/>.
/// </summary>
public sealed class StubDnsClientOptions
{
    /// <summary>
    /// The single transport the client speaks to. Required.
    /// </summary>
    public DnsTransport? Transport { get; set; }

    /// <summary>
    /// Per-call timeout. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sets the RD flag on outgoing queries. <see langword="true"/> when the stub talks to a
    /// recursive upstream; <see langword="false"/> when it talks to an authoritative server
    /// (iterative resolution path). Defaults to <see langword="true"/> &#8211; the common stub
    /// case.
    /// </summary>
    public bool RecursionDesired { get; set; } = true;

    /// <summary>
    /// EDNS UDP payload size to advertise via OPT (RFC 6891 &#167; 6.2.3). Defaults to 1232
    /// octets. Set to zero to omit the OPT record entirely.
    /// </summary>
    public ushort EdnsPayloadSize { get; set; } = 1232;
}
