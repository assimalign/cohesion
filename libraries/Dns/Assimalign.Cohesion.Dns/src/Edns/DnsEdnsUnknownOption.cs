using System;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// An EDNS option whose <see cref="DnsEdnsOption.Code"/> isn't recognized by this library.
/// Preserves the opaque payload bytes so the OPT record round-trips per RFC 6891 &#167; 4.
/// </summary>
public sealed class DnsEdnsUnknownOption : DnsEdnsOption
{
    private readonly byte[] _payload;

    /// <summary>
    /// Initializes a new <see cref="DnsEdnsUnknownOption"/>.
    /// </summary>
    public DnsEdnsUnknownOption(DnsEdnsOptionCode code, ReadOnlySpan<byte> payload)
        : base(code)
    {
        _payload = payload.ToArray();
    }

    /// <summary>The opaque payload bytes preserved from the wire.</summary>
    public ReadOnlySpan<byte> Payload => _payload;

    /// <inheritdoc />
    internal override void WritePayload(ref DnsWireWriter writer)
        => writer.WriteBytes(_payload);
}
