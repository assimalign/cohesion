using System;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// The fixed 12-octet header that prefixes every DNS message on the wire (RFC 1035
/// &#167; 4.1.1). Carries the transaction identifier, the OPCODE / RCODE,
/// <see cref="DnsHeaderFlags"/>, and the four section counts.
/// </summary>
/// <remarks>
/// <para>
/// The wire layout is:
/// </para>
/// <pre>
///                                 1  1  1  1  1  1
///   0  1  2  3  4  5  6  7  8  9  0  1  2  3  4  5
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// |                      ID                       |
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// |QR|   Opcode  |AA|TC|RD|RA|   Z    |   RCODE   |
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// |                    QDCOUNT                    |
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// |                    ANCOUNT                    |
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// |                    NSCOUNT                    |
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// |                    ARCOUNT                    |
/// +--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+--+
/// </pre>
/// <para>
/// The <c>Z</c> field is reserved (must be zero) except for the AD and CD bits added by
/// DNSSEC (RFC 4035 §3.2); <see cref="DnsHeaderFlags"/> exposes both.
/// </para>
/// </remarks>
public readonly struct DnsHeader : IEquatable<DnsHeader>
{
    /// <summary>
    /// On-wire size of the header in octets. Every DNS message starts with this many bytes.
    /// </summary>
    public const int WireSize = 12;

    /// <summary>Initializes a new header.</summary>
    public DnsHeader(
        ushort id,
        DnsHeaderFlags flags,
        DnsOpCode opCode,
        DnsResponseCode responseCode,
        ushort questionCount,
        ushort answerCount,
        ushort authorityCount,
        ushort additionalCount)
    {
        Id = id;
        Flags = flags;
        OpCode = opCode;
        ResponseCode = responseCode;
        QuestionCount = questionCount;
        AnswerCount = answerCount;
        AuthorityCount = authorityCount;
        AdditionalCount = additionalCount;
    }

    /// <summary>16-bit transaction identifier. Echoed by the responder so the client can
    /// correlate the response with its query.</summary>
    public ushort Id { get; }

    /// <summary>Single-bit flags carried in the second 16-bit word.</summary>
    public DnsHeaderFlags Flags { get; }

    /// <summary>4-bit operation code (RFC 1035 §4.1.1).</summary>
    public DnsOpCode OpCode { get; }

    /// <summary>4-bit response code (RFC 1035 §4.1.1). Extended to twelve bits via the EDNS
    /// OPT pseudo-record; the high eight bits live there.</summary>
    public DnsResponseCode ResponseCode { get; }

    /// <summary>Number of entries in the question section.</summary>
    public ushort QuestionCount { get; }

    /// <summary>Number of entries in the answer section.</summary>
    public ushort AnswerCount { get; }

    /// <summary>Number of entries in the authority section.</summary>
    public ushort AuthorityCount { get; }

    /// <summary>Number of entries in the additional section.</summary>
    public ushort AdditionalCount { get; }

    /// <summary>
    /// Reads a 12-octet header from <paramref name="buffer"/>.
    /// </summary>
    /// <exception cref="DnsException">The buffer is shorter than <see cref="WireSize"/> or
    /// uses a reserved bit pattern.</exception>
    public static DnsHeader Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < WireSize)
        {
            DnsException.ThrowMalformed($"header requires {WireSize} octets but only {buffer.Length} available");
        }

        var reader = new DnsWireReader(buffer);
        ushort id = reader.ReadUInt16();
        ushort flagsWord = reader.ReadUInt16();
        ushort qdcount = reader.ReadUInt16();
        ushort ancount = reader.ReadUInt16();
        ushort nscount = reader.ReadUInt16();
        ushort arcount = reader.ReadUInt16();

        DnsOpCode opCode = (DnsOpCode)((flagsWord >> 11) & 0x0F);
        DnsResponseCode rcode = (DnsResponseCode)(flagsWord & 0x0F);

        // Strip OPCODE + RCODE from the flags word; the rest is the boolean-flag mask.
        DnsHeaderFlags flags = (DnsHeaderFlags)(flagsWord & 0b1000_0111_1011_0000);

        return new DnsHeader(id, flags, opCode, rcode, qdcount, ancount, nscount, arcount);
    }

    /// <summary>
    /// Writes this header to <paramref name="buffer"/>. The buffer must have at least
    /// <see cref="WireSize"/> octets available.
    /// </summary>
    public void WriteTo(Span<byte> buffer)
    {
        var writer = new DnsWireWriter(buffer);
        WriteTo(ref writer);
    }

    internal void WriteTo(ref DnsWireWriter writer)
    {
        writer.WriteUInt16(Id);

        ushort flagsWord = (ushort)Flags;
        flagsWord |= (ushort)(((byte)OpCode & 0x0F) << 11);
        flagsWord |= (ushort)((ushort)ResponseCode & 0x0F);
        writer.WriteUInt16(flagsWord);

        writer.WriteUInt16(QuestionCount);
        writer.WriteUInt16(AnswerCount);
        writer.WriteUInt16(AuthorityCount);
        writer.WriteUInt16(AdditionalCount);
    }

    /// <inheritdoc />
    public bool Equals(DnsHeader other)
        => Id == other.Id
        && Flags == other.Flags
        && OpCode == other.OpCode
        && ResponseCode == other.ResponseCode
        && QuestionCount == other.QuestionCount
        && AnswerCount == other.AnswerCount
        && AuthorityCount == other.AuthorityCount
        && AdditionalCount == other.AdditionalCount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DnsHeader other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Id, Flags, OpCode, ResponseCode,
            QuestionCount, AnswerCount, AuthorityCount, AdditionalCount);

    public static bool operator ==(DnsHeader left, DnsHeader right) => left.Equals(right);
    public static bool operator !=(DnsHeader left, DnsHeader right) => !left.Equals(right);
}
