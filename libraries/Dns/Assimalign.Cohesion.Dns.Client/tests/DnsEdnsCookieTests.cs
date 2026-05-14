using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns.Tests;

/// <summary>
/// EDNS Cookie (RFC 7873) integration tests. Verify that resolvers attach the cookie option
/// to outgoing queries, cache the server cookie returned by the upstream, attach the
/// (client + server) pair on subsequent queries, and retry once on BADCOOKIE.
/// </summary>
public sealed class DnsEdnsCookieTests
{
    private static readonly byte[] FixedClientCookie =
        new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF };

    private static readonly byte[] ServerCookie =
        new byte[] { 0xFE, 0xDC, 0xBA, 0x98, 0x76, 0x54, 0x32, 0x10 };

    [Fact]
    public async Task StubClient_AttachesClientCookieToOutgoingQuery()
    {
        DnsEdnsCookieOption? captured = null;
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsMessage.Parse(req);
            captured = DnsTestMessages.ExtractCookie(query);
            return DnsTestMessages.BuildAnswer(query, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var stub = new StubDnsClient(new StubDnsClientOptions
        {
            Transport = udp,
            EdnsClientCookie = FixedClientCookie,
        });

        _ = await stub.QueryAsync(new DnsQuestion("example.com", DnsRecordType.A));

        Assert.NotNull(captured);
        Assert.Equal(FixedClientCookie, captured!.ClientCookie.ToArray());
        Assert.False(captured.HasServerCookie); // first query has no server cookie yet
    }

    [Fact]
    public async Task StubClient_DisablingCookies_OmitsOption()
    {
        DnsEdnsCookieOption? captured = null;
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsMessage.Parse(req);
            captured = DnsTestMessages.ExtractCookie(query);
            return DnsTestMessages.BuildAnswer(query, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var stub = new StubDnsClient(new StubDnsClientOptions
        {
            Transport = udp,
            EnableEdnsCookies = false,
        });

        _ = await stub.QueryAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.Null(captured);
    }

    [Fact]
    public async Task StubClient_CachesServerCookie_AndEchoesItOnSubsequentQuery()
    {
        var captured = new List<DnsEdnsCookieOption?>();
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsMessage.Parse(req);
            captured.Add(DnsTestMessages.ExtractCookie(query));
            return DnsTestMessages.BuildAnswerWithCookie(
                query,
                new DnsRecord[] { DnsTestMessages.ExampleComA() },
                clientCookieEcho: FixedClientCookie,
                serverCookie: ServerCookie);
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var stub = new StubDnsClient(new StubDnsClientOptions
        {
            Transport = udp,
            EdnsClientCookie = FixedClientCookie,
        });

        _ = await stub.QueryAsync(new DnsQuestion("example.com", DnsRecordType.A));
        _ = await stub.QueryAsync(new DnsQuestion("example2.com", DnsRecordType.A));

        Assert.Equal(2, captured.Count);
        Assert.NotNull(captured[0]);
        Assert.False(captured[0]!.HasServerCookie);
        Assert.NotNull(captured[1]);
        Assert.True(captured[1]!.HasServerCookie);
        Assert.Equal(ServerCookie, captured[1]!.ServerCookie.ToArray());
        Assert.Equal(FixedClientCookie, captured[1]!.ClientCookie.ToArray());
    }

    [Fact]
    public async Task StubClient_BadCookieResponse_TriggersOneRetry()
    {
        int requestCount = 0;
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            requestCount++;
            DnsMessage query = DnsMessage.Parse(req);
            if (requestCount == 1)
            {
                // First request: BADCOOKIE with a server cookie attached.
                return DnsTestMessages.BuildAnswerWithCookie(
                    query,
                    Array.Empty<DnsRecord>(),
                    clientCookieEcho: FixedClientCookie,
                    serverCookie: ServerCookie,
                    rcode: DnsResponseCode.BadCookie);
            }
            // Second request: accept with the server cookie + return real answer.
            DnsEdnsCookieOption? cookie = DnsTestMessages.ExtractCookie(query);
            Assert.NotNull(cookie);
            Assert.True(cookie!.HasServerCookie);
            return DnsTestMessages.BuildAnswerWithCookie(
                query,
                new DnsRecord[] { DnsTestMessages.ExampleComA() },
                clientCookieEcho: FixedClientCookie,
                serverCookie: ServerCookie);
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var stub = new StubDnsClient(new StubDnsClientOptions
        {
            Transport = udp,
            EdnsClientCookie = FixedClientCookie,
        });

        DnsMessage answer = await stub.QueryAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.Single(answer.Answers);
        Assert.Equal(2, requestCount); // one BADCOOKIE + one retry
    }

    [Fact]
    public async Task StubClient_RejectsWrongClientCookieEcho_DoesNotCacheServerCookie()
    {
        // A malicious or buggy server echoes the wrong client cookie back. The store must
        // ignore the server cookie attached to that response — subsequent queries still send
        // a client-cookie-only option.
        var captured = new List<DnsEdnsCookieOption?>();
        byte[] wrongEcho = (byte[])FixedClientCookie.Clone();
        wrongEcho[0] ^= 0xFF;

        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsMessage.Parse(req);
            captured.Add(DnsTestMessages.ExtractCookie(query));
            return DnsTestMessages.BuildAnswerWithCookie(
                query,
                new DnsRecord[] { DnsTestMessages.ExampleComA() },
                clientCookieEcho: wrongEcho,
                serverCookie: ServerCookie);
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var stub = new StubDnsClient(new StubDnsClientOptions
        {
            Transport = udp,
            EdnsClientCookie = FixedClientCookie,
        });

        _ = await stub.QueryAsync(new DnsQuestion("example.com", DnsRecordType.A));
        _ = await stub.QueryAsync(new DnsQuestion("example2.com", DnsRecordType.A));

        // Both queries should send a client-cookie-only option (no server cookie cached).
        Assert.All(captured, c =>
        {
            Assert.NotNull(c);
            Assert.False(c!.HasServerCookie);
        });
    }

    [Fact]
    public async Task ForwardingResolver_AttachesCookieAndHandlesBadCookie()
    {
        int requestCount = 0;
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            requestCount++;
            DnsMessage query = DnsMessage.Parse(req);
            if (requestCount == 1)
            {
                return DnsTestMessages.BuildAnswerWithCookie(
                    query,
                    Array.Empty<DnsRecord>(),
                    clientCookieEcho: FixedClientCookie,
                    serverCookie: ServerCookie,
                    rcode: DnsResponseCode.BadCookie);
            }
            return DnsTestMessages.BuildAnswerWithCookie(
                query,
                new DnsRecord[] { DnsTestMessages.ExampleComA() },
                clientCookieEcho: FixedClientCookie,
                serverCookie: ServerCookie);
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            EdnsClientCookie = FixedClientCookie,
        });

        DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.Single(answer.Answers);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task IterativeResolver_AttachesCookieOnEveryStep()
    {
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        await using var example = new LoopbackDnsAuthority("example.com");
        root.Delegate("com", "ns1.com", com);
        com.Delegate("example.com", "ns1.example.com", example);
        example.AddRecord(new DnsARecord("www.example.com", IPAddress.Parse("93.184.216.34"), 300));

        var observedCookies = new List<DnsEdnsCookieOption?>();
        root.OnInboundQuery = m => observedCookies.Add(DnsTestMessages.ExtractCookie(m));
        com.OnInboundQuery = m => observedCookies.Add(DnsTestMessages.ExtractCookie(m));
        example.OnInboundQuery = m => observedCookies.Add(DnsTestMessages.ExtractCookie(m));

        var resolver = new IterativeDnsResolver(new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
            EdnsClientCookie = FixedClientCookie,
        });

        _ = await resolver.ResolveAsync(new DnsQuestion("www.example.com", DnsRecordType.A));

        // Every authority that received a request saw a cookie carrying our client cookie.
        Assert.NotEmpty(observedCookies);
        Assert.All(observedCookies, c =>
        {
            Assert.NotNull(c);
            Assert.Equal(FixedClientCookie, c!.ClientCookie.ToArray());
        });
    }
}
