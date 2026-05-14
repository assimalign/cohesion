using System;
using Assimalign.Cohesion.Dns;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// Covers the 12-octet DNS header wire format (RFC 1035 §4.1.1) round-trip + the field-
/// packing rules (OPCODE in bits 11-14, RCODE in the low 4 bits, AD/CD in bits 4-5).
/// </summary>
public class DnsHeaderTests
{
    [Fact(DisplayName = "Cohesion Test [Dns] - Header: round-trip preserves every field")]
    public void RoundTrip_Identity()
    {
        var header = new DnsHeader(
            id: 0xBEEF,
            flags: DnsHeaderFlags.Response | DnsHeaderFlags.RecursionDesired | DnsHeaderFlags.RecursionAvailable,
            opCode: DnsOpCode.Query,
            responseCode: DnsResponseCode.NoError,
            questionCount: 1,
            answerCount: 2,
            authorityCount: 3,
            additionalCount: 4);

        Span<byte> buffer = stackalloc byte[DnsHeader.WireSize];
        header.WriteTo(buffer);

        var parsed = DnsHeader.Parse(buffer);
        Assert.Equal(header, parsed);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Header: OPCODE occupies bits 11-14")]
    public void OpCode_BitPosition()
    {
        var header = new DnsHeader(
            id: 0,
            flags: DnsHeaderFlags.None,
            opCode: DnsOpCode.Update, // 5
            responseCode: DnsResponseCode.NoError,
            0, 0, 0, 0);

        Span<byte> buffer = stackalloc byte[DnsHeader.WireSize];
        header.WriteTo(buffer);

        // Second 16-bit word (bytes 2-3): the OPCODE (5) sits in bits 11-14
        // => 0b0010_1000_0000_0000 = 0x2800
        Assert.Equal(0x28, buffer[2]);
        Assert.Equal(0x00, buffer[3]);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Header: RCODE occupies the low 4 bits")]
    public void RCode_BitPosition()
    {
        var header = new DnsHeader(
            id: 0,
            flags: DnsHeaderFlags.None,
            opCode: DnsOpCode.Query,
            responseCode: DnsResponseCode.NXDomain, // 3
            0, 0, 0, 0);

        Span<byte> buffer = stackalloc byte[DnsHeader.WireSize];
        header.WriteTo(buffer);

        Assert.Equal(0x00, buffer[2]);
        Assert.Equal(0x03, buffer[3]);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Header: DNSSEC AD + CD bits round-trip")]
    public void DnssecFlags_RoundTrip()
    {
        var header = new DnsHeader(
            id: 0x1234,
            flags: DnsHeaderFlags.AuthenticData | DnsHeaderFlags.CheckingDisabled,
            opCode: DnsOpCode.Query,
            responseCode: DnsResponseCode.NoError,
            0, 0, 0, 0);

        Span<byte> buffer = stackalloc byte[DnsHeader.WireSize];
        header.WriteTo(buffer);

        var parsed = DnsHeader.Parse(buffer);
        Assert.True(parsed.Flags.HasFlag(DnsHeaderFlags.AuthenticData));
        Assert.True(parsed.Flags.HasFlag(DnsHeaderFlags.CheckingDisabled));
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - Header: under-sized buffer throws Malformed")]
    public void TooShortBuffer_Throws()
    {
        Span<byte> buffer = stackalloc byte[11];
        // Have to copy out since Span can't be captured in lambdas. Use array to test.
        var arr = new byte[11];
        var ex = Assert.Throws<DnsException>(() => DnsHeader.Parse(arr));
        Assert.Equal(DnsErrorCode.Malformed, ex.Code);
    }
}
