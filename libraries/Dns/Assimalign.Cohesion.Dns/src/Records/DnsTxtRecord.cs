using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Assimalign.Cohesion.Dns.Internal;

namespace Assimalign.Cohesion.Dns;

/// <summary>
/// A text (<c>TXT</c>) record &#8211; RFC 1035 &#167; 3.3.14. The RDATA is a sequence of one
/// or more length-prefixed character strings (each &#8804; 255 octets). Used for arbitrary
/// text data: SPF, DKIM, domain-ownership verification, and anything else.
/// </summary>
public sealed class DnsTxtRecord : DnsRecord
{
    /// <summary>
    /// Initializes a new <c>TXT</c> record with one or more strings.
    /// </summary>
    /// <param name="name">The owner name.</param>
    /// <param name="strings">The text strings, copied at construction. Each must be 0&#8211;255 octets in UTF-8.</param>
    /// <param name="timeToLive">The TTL in seconds.</param>
    /// <param name="class">The DNS class. Defaults to <see cref="DnsClass.IN"/>.</param>
    public DnsTxtRecord(
        DnsName name,
        IReadOnlyList<string> strings,
        uint timeToLive,
        DnsClass @class = DnsClass.IN)
        : base(name, DnsRecordType.TXT, @class, timeToLive)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var snapshot = strings.ToArray();
        if (snapshot.Length == 0)
        {
            throw new ArgumentException(ErrorMessages.TxtStringsEmpty, nameof(strings));
        }

        foreach (var s in snapshot)
        {
            if (s is null)
            {
                throw new ArgumentException(ErrorMessages.TxtStringNull, nameof(strings));
            }
            int byteCount = Encoding.UTF8.GetByteCount(s);
            if (byteCount > 255)
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.TxtStringTooLong, byteCount),
                    nameof(strings));
            }
        }
        Strings = Array.AsReadOnly(snapshot);
    }

    /// <summary>
    /// Convenience constructor for the common single-string case.
    /// </summary>
    public DnsTxtRecord(DnsName name, string text, uint timeToLive, DnsClass @class = DnsClass.IN)
        : this(name, new[] { text }, timeToLive, @class)
    {
    }

    /// <summary>A read-only snapshot of the strings carried in the RDATA, in their declared order.</summary>
    public IReadOnlyList<string> Strings { get; }

    /// <inheritdoc />
    internal override void WriteRData(ref DnsWireWriter writer)
    {
        foreach (var s in Strings)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            writer.WriteUInt8((byte)bytes.Length);
            writer.WriteBytes(bytes);
        }
    }

    internal static DnsTxtRecord ReadRData(
        DnsName name,
        DnsClass @class,
        uint ttl,
        ref DnsWireReader reader,
        ushort rdLength)
    {
        var strings = new List<string>();
        int rdataStart = reader.Position;
        while (reader.Position - rdataStart < rdLength)
        {
            byte length = reader.ReadUInt8();
            ReadOnlySpan<byte> bytes = reader.ReadBytes(length);
            strings.Add(Encoding.UTF8.GetString(bytes));
        }
        int consumed = reader.Position - rdataStart;
        if (consumed != rdLength)
        {
            DnsException.ThrowMalformed(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.TxtRDataLengthMismatch, rdLength, consumed));
        }
        if (strings.Count == 0)
        {
            DnsException.ThrowMalformed(ErrorMessages.TxtRDataEmpty);
        }
        return new DnsTxtRecord(name, strings, ttl, @class);
    }
}
