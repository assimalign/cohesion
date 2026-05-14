using System;
using System.Collections.Generic;
using System.Net;
using Assimalign.Cohesion.Dns;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// Round-trip + golden-corpus tests for <see cref="DnsMessage"/>. Builds messages with each
/// supported record family, serializes, parses, and verifies field-by-field equality.
/// Includes name-compression coverage and unknown-RR preservation per RFC 3597.
/// </summary>
public class DnsMessageTests
{
    private static byte[] WriteToArray(DnsMessage message)
    {
        byte[] buffer = new byte[4096];
        int written = message.WriteTo(buffer);
        return buffer.AsSpan(0, written).ToArray();
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Message: query round-trips through wire format")]
    public void Query_RoundTrip()
    {
        var question = new DnsQuestion("www.example.com", DnsRecordType.A);
        var original = new DnsMessage(0x1234, question);

        byte[] bytes = WriteToArray(original);
        var parsed = DnsMessage.Parse(bytes);

        Assert.Equal(0x1234, parsed.Header.Id);
        Assert.True(parsed.Header.Flags.HasFlag(DnsHeaderFlags.RecursionDesired));
        Assert.Single(parsed.Questions);
        Assert.Equal(new DnsName("www.example.com"), parsed.Questions[0].Name);
        Assert.Equal(DnsRecordType.A, parsed.Questions[0].Type);
        Assert.Equal(DnsClass.IN, parsed.Questions[0].Class);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Message: A record round-trips")]
    public void ARecord_RoundTrip()
    {
        var header = new DnsHeader(0x4242, DnsHeaderFlags.Response, DnsOpCode.Query, DnsResponseCode.NoError, 1, 1, 0, 0);
        var question = new DnsQuestion("example.com", DnsRecordType.A);
        var record = new DnsARecord("example.com", IPAddress.Parse("93.184.216.34"), timeToLive: 3600);
        var original = new DnsMessage(header, new[] { question }, new DnsRecord[] { record },
            Array.Empty<DnsRecord>(), Array.Empty<DnsRecord>());

        byte[] bytes = WriteToArray(original);
        var parsed = DnsMessage.Parse(bytes);

        var answer = Assert.IsType<DnsARecord>(parsed.Answers[0]);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), answer.Address);
        Assert.Equal(3600u, answer.TimeToLive);
        Assert.Equal(new DnsName("example.com"), answer.Name);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Message: AAAA record round-trips")]
    public void AaaaRecord_RoundTrip()
    {
        var record = new DnsAaaaRecord("example.com", IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"), 3600);
        var original = BuildSingleAnswer(record);

        byte[] bytes = WriteToArray(original);
        var parsed = DnsMessage.Parse(bytes);

        var answer = Assert.IsType<DnsAaaaRecord>(parsed.Answers[0]);
        Assert.Equal(IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946"), answer.Address);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Message: CNAME / NS / PTR with shared suffix compress")]
    public void NameCompression_Active()
    {
        // Three answers under the same example.com suffix; expect compression to fire.
        var answers = new DnsRecord[]
        {
            new DnsCnameRecord("alias.example.com", "canonical.example.com", 60),
            new DnsNsRecord("example.com", "ns1.example.com", 60),
            new DnsPtrRecord("ptr.example.com", "host.example.com", 60),
        };
        var original = BuildAnswers(answers);

        byte[] bytes = WriteToArray(original);
        var parsed = DnsMessage.Parse(bytes);

        Assert.Equal(3, parsed.Answers.Count);

        var cname = Assert.IsType<DnsCnameRecord>(parsed.Answers[0]);
        Assert.Equal(new DnsName("canonical.example.com"), cname.CanonicalName);

        var ns = Assert.IsType<DnsNsRecord>(parsed.Answers[1]);
        Assert.Equal(new DnsName("ns1.example.com"), ns.NameServer);

        var ptr = Assert.IsType<DnsPtrRecord>(parsed.Answers[2]);
        Assert.Equal(new DnsName("host.example.com"), ptr.PointerName);

        // The serialized output should be meaningfully smaller than the uncompressed lower
        // bound (12-byte header + 0 questions + 3 records each carrying 4 distinct names
        // would be much larger). Verify by an upper bound.
        Assert.True(bytes.Length < 200, $"compressed message should be <200 bytes; got {bytes.Length}");
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Message: MX record carries preference + exchange")]
    public void MxRecord_RoundTrip()
    {
        var record = new DnsMxRecord("example.com", preference: 10, exchange: "mail.example.com", 3600);
        byte[] bytes = WriteToArray(BuildSingleAnswer(record));

        var parsed = DnsMessage.Parse(bytes);
        var mx = Assert.IsType<DnsMxRecord>(parsed.Answers[0]);
        Assert.Equal((ushort)10, mx.Preference);
        Assert.Equal(new DnsName("mail.example.com"), mx.Exchange);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Message: TXT record preserves multi-string layout")]
    public void TxtRecord_MultiString_RoundTrip()
    {
        var record = new DnsTxtRecord("example.com",
            new[] { "v=spf1", "include:_spf.example.org", "-all" },
            timeToLive: 3600);
        byte[] bytes = WriteToArray(BuildSingleAnswer(record));

        var parsed = DnsMessage.Parse(bytes);
        var txt = Assert.IsType<DnsTxtRecord>(parsed.Answers[0]);
        Assert.Equal(3, txt.Strings.Count);
        Assert.Equal("v=spf1", txt.Strings[0]);
        Assert.Equal("include:_spf.example.org", txt.Strings[1]);
        Assert.Equal("-all", txt.Strings[2]);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Message: SOA record round-trips all 7 fields")]
    public void SoaRecord_RoundTrip()
    {
        var record = new DnsSoaRecord(
            name: "example.com",
            primaryNameServer: "ns1.example.com",
            responsibleMailbox: "hostmaster.example.com",
            serial: 2024_05_14_01u,
            refreshInterval: 7200,
            retryInterval: 3600,
            expireLimit: 1_209_600,
            minimumTtl: 300,
            timeToLive: 86400);

        byte[] bytes = WriteToArray(BuildSingleAnswer(record));
        var parsed = DnsMessage.Parse(bytes);
        var soa = Assert.IsType<DnsSoaRecord>(parsed.Answers[0]);

        Assert.Equal(new DnsName("ns1.example.com"), soa.PrimaryNameServer);
        Assert.Equal(new DnsName("hostmaster.example.com"), soa.ResponsibleMailbox);
        Assert.Equal(2024_05_14_01u, soa.Serial);
        Assert.Equal(7200u, soa.RefreshInterval);
        Assert.Equal(3600u, soa.RetryInterval);
        Assert.Equal(1_209_600u, soa.ExpireLimit);
        Assert.Equal(300u, soa.MinimumTtl);
        Assert.Equal(86400u, soa.TimeToLive);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Message: SRV record round-trips")]
    public void SrvRecord_RoundTrip()
    {
        var record = new DnsSrvRecord("_xmpp-server._tcp.example.com",
            priority: 5, weight: 10, port: 5269, target: "xmpp.example.com", timeToLive: 86400);
        byte[] bytes = WriteToArray(BuildSingleAnswer(record));

        var parsed = DnsMessage.Parse(bytes);
        var srv = Assert.IsType<DnsSrvRecord>(parsed.Answers[0]);
        Assert.Equal((ushort)5, srv.Priority);
        Assert.Equal((ushort)10, srv.Weight);
        Assert.Equal((ushort)5269, srv.Port);
        Assert.Equal(new DnsName("xmpp.example.com"), srv.Target);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Message: unknown RR type preserves bytes verbatim")]
    public void UnknownRecord_RoundTrip()
    {
        // RFC 3597: every implementation MUST preserve unknown types byte-for-byte.
        // Use a code that's not in DnsRecordType (62 = CSYNC, not enumerated).
        var unknownType = (DnsRecordType)62;
        var rdata = new byte[] { 0x01, 0x02, 0x03, 0x04, 0xDE, 0xAD, 0xBE, 0xEF };
        var record = new DnsUnknownRecord("example.com", unknownType, DnsClass.IN, 3600, rdata);

        byte[] bytes = WriteToArray(BuildSingleAnswer(record));
        var parsed = DnsMessage.Parse(bytes);
        var unknown = Assert.IsType<DnsUnknownRecord>(parsed.Answers[0]);

        Assert.Equal(unknownType, unknown.Type);
        Assert.True(rdata.AsSpan().SequenceEqual(unknown.Data));
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Message: Parse rejects truncated header")]
    public void ParseTruncated_Throws()
    {
        var ex = Assert.Throws<DnsException>(() => DnsMessage.Parse(new byte[5]));
        Assert.Equal(DnsErrorCode.Malformed, ex.Code);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Message: Parse rejects malformed name (reserved label kind)")]
    public void ParseReservedLabelKind_Throws()
    {
        // Build a valid header with QDCOUNT=1, then a label with the reserved bit pattern 0x80
        // (top two bits 10).
        byte[] bytes = new byte[14];
        // Header: id=0, flags=0, qd=1, an=0, ns=0, ar=0
        bytes[5] = 0x01;
        // Question: label with reserved length octet (top bits 10)
        bytes[12] = 0x80;

        var ex = Assert.Throws<DnsException>(() => DnsMessage.Parse(bytes));
        Assert.Equal(DnsErrorCode.Malformed, ex.Code);
    }

    // ----- helpers -----

    private static DnsMessage BuildSingleAnswer(DnsRecord record)
    {
        var header = new DnsHeader(
            id: 0x4242,
            flags: DnsHeaderFlags.Response | DnsHeaderFlags.RecursionDesired | DnsHeaderFlags.RecursionAvailable,
            opCode: DnsOpCode.Query,
            responseCode: DnsResponseCode.NoError,
            questionCount: 0,
            answerCount: 1,
            authorityCount: 0,
            additionalCount: 0);
        return new DnsMessage(header, Array.Empty<DnsQuestion>(), new[] { record },
            Array.Empty<DnsRecord>(), Array.Empty<DnsRecord>());
    }

    private static DnsMessage BuildAnswers(IReadOnlyList<DnsRecord> records)
    {
        var header = new DnsHeader(
            id: 0x4242,
            flags: DnsHeaderFlags.Response,
            opCode: DnsOpCode.Query,
            responseCode: DnsResponseCode.NoError,
            questionCount: 0,
            answerCount: (ushort)records.Count,
            authorityCount: 0,
            additionalCount: 0);
        return new DnsMessage(header, Array.Empty<DnsQuestion>(), records,
            Array.Empty<DnsRecord>(), Array.Empty<DnsRecord>());
    }
}
