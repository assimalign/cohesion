using System;
using System.Collections.Generic;
using System.Net;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// Helpers for constructing typed DNS request / response messages from raw bytes in tests.
/// </summary>
internal static class DnsTestMessages
{
    /// <summary>
    /// Parses an inbound query into a <see cref="DnsMessage"/>.
    /// </summary>
    public static DnsMessage ParseQuery(ReadOnlySpan<byte> request) => DnsMessage.Parse(request);

    /// <summary>
    /// Builds a NoError response that echoes the query's id + question and carries the supplied
    /// answer records. <paramref name="authoritative"/> sets the AA flag.
    /// </summary>
    public static byte[] BuildAnswer(
        DnsMessage query,
        IReadOnlyList<DnsRecord> answers,
        bool truncated = false,
        bool authoritative = false,
        IReadOnlyList<DnsRecord>? authorities = null,
        IReadOnlyList<DnsRecord>? additionals = null)
    {
        DnsHeaderFlags flags = DnsHeaderFlags.Response | DnsHeaderFlags.RecursionAvailable;
        if (truncated)
        {
            flags |= DnsHeaderFlags.Truncated;
        }
        if (authoritative)
        {
            flags |= DnsHeaderFlags.AuthoritativeAnswer;
        }

        var header = new DnsHeader(
            query.Header.Id,
            flags,
            DnsOpCode.Query,
            DnsResponseCode.NoError,
            (ushort)query.Questions.Count,
            (ushort)answers.Count,
            (ushort)(authorities?.Count ?? 0),
            (ushort)(additionals?.Count ?? 0));

        var response = new DnsMessage(
            header,
            query.Questions,
            answers,
            authorities ?? Array.Empty<DnsRecord>(),
            additionals ?? Array.Empty<DnsRecord>());

        return Serialize(response);
    }

    /// <summary>
    /// Builds an NXDOMAIN response with the supplied SOA authority (for RFC 2308 negative
    /// caching).
    /// </summary>
    public static byte[] BuildNxDomain(DnsMessage query, DnsSoaRecord soa)
    {
        var header = new DnsHeader(
            query.Header.Id,
            DnsHeaderFlags.Response | DnsHeaderFlags.RecursionAvailable,
            DnsOpCode.Query,
            DnsResponseCode.NXDomain,
            (ushort)query.Questions.Count,
            0,
            1,
            0);

        var response = new DnsMessage(
            header,
            query.Questions,
            Array.Empty<DnsRecord>(),
            new DnsRecord[] { soa },
            Array.Empty<DnsRecord>());

        return Serialize(response);
    }

    /// <summary>
    /// Builds a response with an explicit RCODE. Useful for SERVFAIL / REFUSED tests.
    /// </summary>
    public static byte[] BuildWithRcode(DnsMessage query, DnsResponseCode rcode)
    {
        var header = new DnsHeader(
            query.Header.Id,
            DnsHeaderFlags.Response | DnsHeaderFlags.RecursionAvailable,
            DnsOpCode.Query,
            rcode,
            (ushort)query.Questions.Count,
            0,
            0,
            0);

        var response = new DnsMessage(
            header,
            query.Questions,
            Array.Empty<DnsRecord>(),
            Array.Empty<DnsRecord>(),
            Array.Empty<DnsRecord>());

        return Serialize(response);
    }

    /// <summary>
    /// Builds a response with the supplied id and question (used for spoof tests where the
    /// response intentionally does not echo the query).
    /// </summary>
    public static byte[] BuildWithCustomIdAndQuestion(
        ushort id,
        DnsQuestion question,
        IReadOnlyList<DnsRecord> answers)
    {
        var header = new DnsHeader(
            id,
            DnsHeaderFlags.Response | DnsHeaderFlags.RecursionAvailable,
            DnsOpCode.Query,
            DnsResponseCode.NoError,
            1,
            (ushort)answers.Count,
            0,
            0);

        var response = new DnsMessage(
            header,
            new[] { question },
            answers,
            Array.Empty<DnsRecord>(),
            Array.Empty<DnsRecord>());

        return Serialize(response);
    }

    public static DnsSoaRecord ExampleComSoa(uint minimumTtl = 60, uint ttl = 300)
        => new(
            name: "example.com",
            primaryNameServer: "ns1.example.com",
            responsibleMailbox: "hostmaster.example.com",
            serial: 1,
            refreshInterval: 7200,
            retryInterval: 3600,
            expireLimit: 1_209_600,
            minimumTtl: minimumTtl,
            timeToLive: ttl);

    public static DnsARecord ExampleComA(string ip = "93.184.216.34", uint ttl = 300)
        => new("example.com", IPAddress.Parse(ip), ttl);

    /// <summary>
    /// Builds a response that attaches an EDNS Cookie option carrying the supplied server
    /// cookie. Used to verify that resolvers cache server cookies for subsequent queries.
    /// </summary>
    public static byte[] BuildAnswerWithCookie(
        DnsMessage query,
        IReadOnlyList<DnsRecord> answers,
        byte[] clientCookieEcho,
        byte[]? serverCookie,
        DnsResponseCode rcode = DnsResponseCode.NoError)
    {
        var cookie = serverCookie is null
            ? new DnsEdnsCookieOption(clientCookieEcho)
            : new DnsEdnsCookieOption(clientCookieEcho, serverCookie);

        // RFC 6891 §6.1.3: RCODEs ≥16 split across the header's low 4 bits and the OPT TTL's
        // upper 8 bits. Encode that split here so a BADCOOKIE (RCODE 23) test produces the
        // right wire bytes.
        int rcodeInt = (int)rcode;
        byte extHigh = (byte)((rcodeInt >> 4) & 0xFF);
        DnsResponseCode lowRcode = (DnsResponseCode)(rcodeInt & 0x0F);

        var opt = new DnsOptRecord(udpPayloadSize: 1232, extendedRCodeHigh: extHigh, version: 0, DnsEdnsFlags.None, new DnsEdnsOption[] { cookie });

        var header = new DnsHeader(
            query.Header.Id,
            DnsHeaderFlags.Response | DnsHeaderFlags.RecursionAvailable,
            DnsOpCode.Query,
            lowRcode,
            (ushort)query.Questions.Count,
            (ushort)answers.Count,
            0,
            1);

        var response = new DnsMessage(
            header,
            query.Questions,
            answers,
            Array.Empty<DnsRecord>(),
            new DnsRecord[] { opt });

        return Serialize(response);
    }

    /// <summary>
    /// Extracts the EDNS Cookie option (if any) from an inbound query. Used in test
    /// responders to verify the client sent the expected client + server cookie pair.
    /// </summary>
    public static DnsEdnsCookieOption? ExtractCookie(DnsMessage message)
    {
        DnsOptRecord? opt = message.Edns;
        if (opt is null)
        {
            return null;
        }
        foreach (DnsEdnsOption option in opt.Options)
        {
            if (option is DnsEdnsCookieOption cookie)
            {
                return cookie;
            }
        }
        return null;
    }

    private static byte[] Serialize(DnsMessage message)
    {
        // 4096 octets is generous for any test response.
        byte[] buffer = new byte[4096];
        int written = message.WriteTo(buffer);
        byte[] trimmed = new byte[written];
        Buffer.BlockCopy(buffer, 0, trimmed, 0, written);
        return trimmed;
    }
}
