using System;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A resource record whose <see cref="DnsRecord.Type"/> isn't recognized by this library.
/// Preserves the opaque RDATA bytes so the message can round-trip unchanged.
/// </summary>
/// <remarks>
/// <para>
/// RFC 3597 (handling of unknown DNS resource record types) is explicit that resolvers and
/// authorities MUST preserve unknown RR types byte-for-byte: future protocol additions
/// would otherwise be impossible without rolling out new code everywhere first. This record
/// satisfies that requirement and is the default fall-through in the wire-format dispatcher.
/// </para>
/// <para>
/// Compression in RDATA is not allowed for unknown types per RFC 3597 §4, so the bytes are
/// emitted verbatim on the wire. The decoder copies the RDATA into a fresh array so the
/// record doesn't pin the source message buffer.
/// </para>
/// </remarks>
public sealed class DnsUnknownRecord : DnsRecord
{
    private readonly byte[] _data;

    /// <summary>
    /// Initializes a new <see cref="DnsUnknownRecord"/>.
    /// </summary>
    /// <param name="name">The owner name.</param>
    /// <param name="type">The (unknown-to-us) record type code.</param>
    /// <param name="class">The DNS class.</param>
    /// <param name="timeToLive">The TTL in seconds.</param>
    /// <param name="data">The opaque RDATA bytes.</param>
    public DnsUnknownRecord(
        DnsName name,
        DnsRecordType type,
        DnsClass @class,
        uint timeToLive,
        ReadOnlySpan<byte> data)
        : base(name, type, @class, timeToLive)
    {
        _data = data.ToArray();
    }

    /// <summary>
    /// The opaque RDATA bytes preserved from the wire.
    /// </summary>
    public ReadOnlySpan<byte> Data => _data;

    /// <inheritdoc />
    internal override void WriteRData(ref DnsWireWriter writer)
        => writer.WriteBytes(_data);

    internal static DnsUnknownRecord ReadRData(
        DnsName name,
        DnsRecordType type,
        DnsClass @class,
        uint ttl,
        ref DnsWireReader reader,
        ushort rdLength)
    {
        ReadOnlySpan<byte> bytes = reader.ReadBytes(rdLength);
        return new DnsUnknownRecord(name, type, @class, ttl, bytes);
    }
}
