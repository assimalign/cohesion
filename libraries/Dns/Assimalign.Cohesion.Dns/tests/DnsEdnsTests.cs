using System;
using System.Net;
using Assimalign.Cohesion.Dns;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// Round-trip and malformed-input coverage for the EDNS OPT pseudo-record and its options
/// (RFC 6891, RFC 7871 ECS, RFC 7873 Cookie, RFC 8914 Extended-DNS-Error).
/// </summary>
public class DnsEdnsTests
{
    private static byte[] WriteToArray(DnsMessage message)
    {
        byte[] buffer = new byte[4096];
        int written = message.WriteTo(buffer);
        return buffer.AsSpan(0, written).ToArray();
    }

    private static DnsMessage BuildWithOpt(DnsOptRecord opt)
    {
        var header = new DnsHeader(
            id: 0x1111,
            flags: DnsHeaderFlags.RecursionDesired,
            opCode: DnsOpCode.Query,
            responseCode: DnsResponseCode.NoError,
            questionCount: 1,
            answerCount: 0,
            authorityCount: 0,
            additionalCount: 1);
        return new DnsMessage(
            header,
            new[] { new DnsQuestion("example.com", DnsRecordType.A) },
            Array.Empty<DnsRecord>(),
            Array.Empty<DnsRecord>(),
            new DnsRecord[] { opt });
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - EDNS: empty OPT round-trips with payload size + DO bit")]
    public void EmptyOpt_RoundTrip()
    {
        var opt = new DnsOptRecord(udpPayloadSize: 1232, flags: DnsEdnsFlags.DnssecOk);
        var message = BuildWithOpt(opt);

        byte[] bytes = WriteToArray(message);
        var parsed = DnsMessage.Parse(bytes);
        var parsedOpt = parsed.Edns;

        Assert.NotNull(parsedOpt);
        Assert.Equal((ushort)1232, parsedOpt!.UdpPayloadSize);
        Assert.Equal(DnsEdnsFlags.DnssecOk, parsedOpt.Flags);
        Assert.Empty(parsedOpt.Options);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - EDNS: ECS option round-trips IPv4 prefix")]
    public void EcsOption_IPv4_RoundTrip()
    {
        var ecs = new DnsEdnsClientSubnetOption(IPAddress.Parse("192.0.2.0"), sourcePrefixLength: 24);
        var opt = new DnsOptRecord(1232, 0, 0, DnsEdnsFlags.None, new DnsEdnsOption[] { ecs });
        byte[] bytes = WriteToArray(BuildWithOpt(opt));

        var parsed = DnsMessage.Parse(bytes);
        var parsedOpt = parsed.Edns!;
        var parsedEcs = Assert.IsType<DnsEdnsClientSubnetOption>(parsedOpt.Options[0]);
        Assert.Equal(IPAddress.Parse("192.0.2.0"), parsedEcs.Address);
        Assert.Equal((byte)24, parsedEcs.SourcePrefixLength);
        Assert.Equal((byte)0, parsedEcs.ScopePrefixLength);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - EDNS: ECS option round-trips IPv6 prefix")]
    public void EcsOption_IPv6_RoundTrip()
    {
        var ecs = new DnsEdnsClientSubnetOption(IPAddress.Parse("2001:db8::"), sourcePrefixLength: 48, scopePrefixLength: 56);
        var opt = new DnsOptRecord(1232, 0, 0, DnsEdnsFlags.None, new DnsEdnsOption[] { ecs });
        byte[] bytes = WriteToArray(BuildWithOpt(opt));

        var parsed = DnsMessage.Parse(bytes);
        var parsedEcs = Assert.IsType<DnsEdnsClientSubnetOption>(parsed.Edns!.Options[0]);
        Assert.Equal(IPAddress.Parse("2001:db8::"), parsedEcs.Address);
        Assert.Equal((byte)48, parsedEcs.SourcePrefixLength);
        Assert.Equal((byte)56, parsedEcs.ScopePrefixLength);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - EDNS: Cookie option (client-only) round-trips")]
    public void CookieOption_ClientOnly_RoundTrip()
    {
        var clientCookie = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        var cookie = new DnsEdnsCookieOption(clientCookie);
        var opt = new DnsOptRecord(1232, 0, 0, DnsEdnsFlags.None, new DnsEdnsOption[] { cookie });

        byte[] bytes = WriteToArray(BuildWithOpt(opt));
        var parsed = DnsMessage.Parse(bytes);
        var parsedCookie = Assert.IsType<DnsEdnsCookieOption>(parsed.Edns!.Options[0]);

        Assert.False(parsedCookie.HasServerCookie);
        Assert.True(clientCookie.AsSpan().SequenceEqual(parsedCookie.ClientCookie));
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - EDNS: Cookie option (client + server) round-trips")]
    public void CookieOption_ClientServer_RoundTrip()
    {
        var clientCookie = new byte[8] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var serverCookie = new byte[16] { 0xaa, 0xbb, 0xcc, 0xdd, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var cookie = new DnsEdnsCookieOption(clientCookie, serverCookie);
        var opt = new DnsOptRecord(1232, 0, 0, DnsEdnsFlags.None, new DnsEdnsOption[] { cookie });

        byte[] bytes = WriteToArray(BuildWithOpt(opt));
        var parsed = DnsMessage.Parse(bytes);
        var parsedCookie = Assert.IsType<DnsEdnsCookieOption>(parsed.Edns!.Options[0]);

        Assert.True(parsedCookie.HasServerCookie);
        Assert.True(clientCookie.AsSpan().SequenceEqual(parsedCookie.ClientCookie));
        Assert.True(serverCookie.AsSpan().SequenceEqual(parsedCookie.ServerCookie));
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - EDNS: Extended-DNS-Error round-trips info-code + text")]
    public void ExtendedError_RoundTrip()
    {
        var ede = new DnsEdnsExtendedErrorOption(infoCode: 6 /* DNSSEC bogus */, extraText: "signature expired");
        var opt = new DnsOptRecord(1232, 0, 0, DnsEdnsFlags.None, new DnsEdnsOption[] { ede });

        byte[] bytes = WriteToArray(BuildWithOpt(opt));
        var parsed = DnsMessage.Parse(bytes);
        var parsedEde = Assert.IsType<DnsEdnsExtendedErrorOption>(parsed.Edns!.Options[0]);

        Assert.Equal((ushort)6, parsedEde.InfoCode);
        Assert.Equal("signature expired", parsedEde.ExtraText);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - EDNS: unknown option code preserves payload bytes")]
    public void UnknownOption_PreservesPayload()
    {
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var unknown = new DnsEdnsUnknownOption((DnsEdnsOptionCode)999, payload);
        var opt = new DnsOptRecord(1232, 0, 0, DnsEdnsFlags.None, new DnsEdnsOption[] { unknown });

        byte[] bytes = WriteToArray(BuildWithOpt(opt));
        var parsed = DnsMessage.Parse(bytes);
        var parsedUnknown = Assert.IsType<DnsEdnsUnknownOption>(parsed.Edns!.Options[0]);

        Assert.Equal((DnsEdnsOptionCode)999, parsedUnknown.Code);
        Assert.True(payload.AsSpan().SequenceEqual(parsedUnknown.Payload));
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - EDNS: multi-option OPT preserves ordering")]
    public void MultipleOptions_OrderPreserved()
    {
        var ecs = new DnsEdnsClientSubnetOption(IPAddress.Parse("192.0.2.0"), 24);
        var cookie = new DnsEdnsCookieOption(new byte[8] { 9, 8, 7, 6, 5, 4, 3, 2 });
        var ede = new DnsEdnsExtendedErrorOption(15);
        var opt = new DnsOptRecord(1232, 0, 0, DnsEdnsFlags.None, new DnsEdnsOption[] { ecs, cookie, ede });

        byte[] bytes = WriteToArray(BuildWithOpt(opt));
        var parsed = DnsMessage.Parse(bytes);
        var options = parsed.Edns!.Options;

        Assert.Equal(3, options.Count);
        Assert.IsType<DnsEdnsClientSubnetOption>(options[0]);
        Assert.IsType<DnsEdnsCookieOption>(options[1]);
        Assert.IsType<DnsEdnsExtendedErrorOption>(options[2]);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - EDNS: extended-rcode high byte round-trips")]
    public void ExtendedRCodeHigh_RoundTrip()
    {
        var opt = new DnsOptRecord(udpPayloadSize: 1232, extendedRCodeHigh: 0x10, version: 0, flags: DnsEdnsFlags.None,
            options: Array.Empty<DnsEdnsOption>());

        byte[] bytes = WriteToArray(BuildWithOpt(opt));
        var parsed = DnsMessage.Parse(bytes);

        Assert.Equal((byte)0x10, parsed.Edns!.ExtendedRCodeHigh);
        Assert.Equal((byte)0, parsed.Edns.Version);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - EDNS: malformed ECS family rejected")]
    public void EcsMalformed_BadFamily_Rejected()
    {
        // Build a hand-crafted OPT carrying an ECS option with family=3 (invalid).
        // We construct it via the wire writer-level path: easier to just check the
        // ReadPayload directly by constructing the message bytes manually.
        // Use the Unknown option to embed the bad bytes, then ensure parsing dispatches
        // through ECS by setting the option code to ClientSubnet.
        byte[] badEcsPayload = new byte[] { 0x00, 0x03 /* bad family */, 0x18 /* source=24 */, 0x00, 0xC0, 0x00, 0x02 };
        var unknown = new DnsEdnsUnknownOption(DnsEdnsOptionCode.ClientSubnet, badEcsPayload);
        var opt = new DnsOptRecord(1232, 0, 0, DnsEdnsFlags.None, new DnsEdnsOption[] { unknown });

        byte[] bytes = WriteToArray(BuildWithOpt(opt));
        var ex = Assert.Throws<DnsException>(() => DnsMessage.Parse(bytes));
        Assert.Equal(DnsErrorCode.Malformed, ex.Code);
    }
}
