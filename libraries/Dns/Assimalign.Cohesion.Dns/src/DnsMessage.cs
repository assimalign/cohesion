using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A complete DNS message &#8211; header plus question, answer, authority, and additional
/// sections (RFC 1035 &#167; 4.1). Supports round-trip serialization through
/// <see cref="Parse"/> and <see cref="WriteTo"/>.
/// </summary>
/// <remarks>
/// <para>
/// Messages are immutable after construction. To synthesize a query, build the message via
/// the constructor and call <see cref="WriteTo"/> against a caller-owned buffer. To consume
/// a response, call <see cref="Parse"/> on the received bytes.
/// </para>
/// <para>
/// The wire format follows RFC 1035 with RFC 6891 extensions (EDNS OPT). Name compression
/// is applied on write per RFC 1035 §4.1.4 and tolerated on read with pointer-chain depth
/// and offset-direction validation.
/// </para>
/// </remarks>
public sealed class DnsMessage
{
    /// <summary>
    /// Initializes a new message from its sections.
    /// </summary>
    public DnsMessage(
        DnsHeader header,
        IReadOnlyList<DnsQuestion> questions,
        IReadOnlyList<DnsRecord> answers,
        IReadOnlyList<DnsRecord> authorities,
        IReadOnlyList<DnsRecord> additionals)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(answers);
        ArgumentNullException.ThrowIfNull(authorities);
        ArgumentNullException.ThrowIfNull(additionals);

        Header = header;
        Questions = questions;
        Answers = answers;
        Authorities = authorities;
        Additionals = additionals;
    }

    /// <summary>
    /// Convenience constructor for a single-question query message.
    /// </summary>
    public DnsMessage(ushort id, DnsQuestion question)
        : this(
            new DnsHeader(
                id,
                DnsHeaderFlags.RecursionDesired,
                DnsOpCode.Query,
                DnsResponseCode.NoError,
                questionCount: 1,
                answerCount: 0,
                authorityCount: 0,
                additionalCount: 0),
            new[] { question },
            Array.Empty<DnsRecord>(),
            Array.Empty<DnsRecord>(),
            Array.Empty<DnsRecord>())
    {
    }

    /// <summary>The 12-octet header.</summary>
    public DnsHeader Header { get; }

    /// <summary>The questions section.</summary>
    public IReadOnlyList<DnsQuestion> Questions { get; }

    /// <summary>The answers section.</summary>
    public IReadOnlyList<DnsRecord> Answers { get; }

    /// <summary>The authority section (NS records, SOA records for NXDomain proofs, etc.).</summary>
    public IReadOnlyList<DnsRecord> Authorities { get; }

    /// <summary>The additional section (glue, OPT, DNSSEC supporting RRs).</summary>
    public IReadOnlyList<DnsRecord> Additionals { get; }

    /// <summary>
    /// Convenience getter that returns the OPT pseudo-record from the additional section, if
    /// any. Per RFC 6891 a message may carry at most one OPT.
    /// </summary>
    public DnsOptRecord? Edns
    {
        get
        {
            foreach (DnsRecord record in Additionals)
            {
                if (record is DnsOptRecord opt)
                {
                    return opt;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Parses a DNS message from <paramref name="buffer"/>.
    /// </summary>
    /// <exception cref="DnsException">The buffer cannot be parsed as a valid DNS message.</exception>
    public static DnsMessage Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < DnsHeader.WireSize)
        {
            DnsException.ThrowMalformed(
                $"DNS message must be at least {DnsHeader.WireSize} octets; got {buffer.Length}");
        }

        DnsHeader header = DnsHeader.Parse(buffer);
        var reader = new DnsWireReader(buffer, DnsHeader.WireSize);

        var questions = new List<DnsQuestion>(header.QuestionCount);
        for (int i = 0; i < header.QuestionCount; i++)
        {
            DnsName name = DnsNameDecoder.Read(ref reader, buffer);
            DnsRecordType type = (DnsRecordType)reader.ReadUInt16();
            DnsClass @class = (DnsClass)reader.ReadUInt16();
            questions.Add(new DnsQuestion(name, type, @class));
        }

        var answers = ReadRecordSection(ref reader, buffer, header.AnswerCount);
        var authorities = ReadRecordSection(ref reader, buffer, header.AuthorityCount);
        var additionals = ReadRecordSection(ref reader, buffer, header.AdditionalCount);

        return new DnsMessage(header, questions, answers, authorities, additionals);
    }

    /// <summary>
    /// Serializes this message into <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">A caller-owned buffer large enough to hold the serialized form
    /// (1232 octets is a safe ceiling for UDP per modern guidance; TCP allows up to 65535).</param>
    /// <param name="bytesWritten">Set to the number of octets written.</param>
    /// <returns><see langword="true"/> on success; <see langword="false"/> when the buffer is too small.</returns>
    public bool TryWriteTo(Span<byte> buffer, out int bytesWritten)
    {
        try
        {
            WriteToCore(buffer, out bytesWritten);
            return true;
        }
        catch (DnsException ex) when (ex.Code == DnsErrorCode.Malformed)
        {
            // The internal writer signals buffer underrun via Malformed; we translate to a
            // false return so callers can size up and retry.
            bytesWritten = 0;
            return false;
        }
    }

    /// <summary>
    /// Serializes this message into <paramref name="buffer"/>. Throws when the buffer is too
    /// small.
    /// </summary>
    public int WriteTo(Span<byte> buffer)
    {
        WriteToCore(buffer, out int written);
        return written;
    }

    private void WriteToCore(Span<byte> buffer, out int bytesWritten)
    {
        var writer = new DnsWireWriter(buffer);

        // Patch the section counts from the actual collection sizes so callers don't have to
        // keep the header in sync with the section lists.
        DnsHeader headerToWrite = new DnsHeader(
            Header.Id,
            Header.Flags,
            Header.OpCode,
            Header.ResponseCode,
            (ushort)Questions.Count,
            (ushort)Answers.Count,
            (ushort)Authorities.Count,
            (ushort)Additionals.Count);
        headerToWrite.WriteTo(ref writer);

        foreach (DnsQuestion question in Questions)
        {
            DnsNameEncoder.Write(ref writer, question.Name);
            writer.WriteUInt16((ushort)question.Type);
            writer.WriteUInt16((ushort)question.Class);
        }

        WriteRecordSection(ref writer, Answers);
        WriteRecordSection(ref writer, Authorities);
        WriteRecordSection(ref writer, Additionals);

        bytesWritten = writer.Position;
    }

    private static List<DnsRecord> ReadRecordSection(
        ref DnsWireReader reader,
        ReadOnlySpan<byte> message,
        ushort count)
    {
        var records = new List<DnsRecord>(count);
        for (int i = 0; i < count; i++)
        {
            DnsName name = DnsNameDecoder.Read(ref reader, message);
            DnsRecordType type = (DnsRecordType)reader.ReadUInt16();
            DnsClass @class = (DnsClass)reader.ReadUInt16();
            uint ttl = reader.ReadUInt32();
            ushort rdLength = reader.ReadUInt16();
            int rdataStart = reader.Position;

            DnsRecord record = type switch
            {
                DnsRecordType.A => DnsARecord.ReadRData(name, @class, ttl, ref reader, rdLength),
                DnsRecordType.AAAA => DnsAaaaRecord.ReadRData(name, @class, ttl, ref reader, rdLength),
                DnsRecordType.CNAME => DnsCnameRecord.ReadRData(name, @class, ttl, ref reader, message, rdataStart, rdLength),
                DnsRecordType.NS => DnsNsRecord.ReadRData(name, @class, ttl, ref reader, message, rdataStart, rdLength),
                DnsRecordType.PTR => DnsPtrRecord.ReadRData(name, @class, ttl, ref reader, message, rdataStart, rdLength),
                DnsRecordType.MX => DnsMxRecord.ReadRData(name, @class, ttl, ref reader, message, rdataStart, rdLength),
                DnsRecordType.TXT => DnsTxtRecord.ReadRData(name, @class, ttl, ref reader, rdLength),
                DnsRecordType.SOA => DnsSoaRecord.ReadRData(name, @class, ttl, ref reader, message, rdataStart, rdLength),
                DnsRecordType.SRV => DnsSrvRecord.ReadRData(name, @class, ttl, ref reader, message, rdataStart, rdLength),
                DnsRecordType.OPT => DnsOptRecord.ReadRData(@class, ttl, ref reader, rdLength),
                _ => DnsUnknownRecord.ReadRData(name, type, @class, ttl, ref reader, rdLength),
            };

            int consumed = reader.Position - rdataStart;
            if (consumed != rdLength)
            {
                DnsException.ThrowMalformed(
                    $"record {type}: declared RDLENGTH {rdLength} but reader consumed {consumed} octets");
            }

            records.Add(record);
        }
        return records;
    }

    private static void WriteRecordSection(ref DnsWireWriter writer, IReadOnlyList<DnsRecord> records)
    {
        foreach (DnsRecord record in records)
        {
            DnsNameEncoder.Write(ref writer, record.Name);
            writer.WriteUInt16((ushort)record.Type);
            writer.WriteUInt16((ushort)record.Class);
            writer.WriteUInt32(record.TimeToLive);

            int rdLengthOffset = writer.Position;
            writer.WriteUInt16(0); // placeholder for RDLENGTH
            int rdataStart = writer.Position;
            record.WriteRData(ref writer);
            int rdLength = writer.Position - rdataStart;
            if (rdLength > ushort.MaxValue)
            {
                DnsException.ThrowMalformed(
                    $"record {record.Type} RDATA exceeds the wire limit (65535 octets); got {rdLength}");
            }
            writer.PatchUInt16(rdLengthOffset, (ushort)rdLength);
        }
    }
}
