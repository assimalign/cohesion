using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns.Tests;

public sealed class ForwardingDnsResolverTests
{
    [Fact]
    public async Task ResolveAsync_RoundTripsAgainstLoopbackResolver()
    {
        await using var server = new LoopbackUdpDnsServer();
        int requestCount = 0;
        server.OnRequest(req =>
        {
            requestCount++;
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildAnswer(query, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(1),
        });

        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

        DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));

        Assert.Single(answer.Answers);
        DnsARecord a = Assert.IsType<DnsARecord>(answer.Answers[0]);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), a.Address);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ResolveAsync_CachesPositiveAnswer()
    {
        await using var server = new LoopbackUdpDnsServer();
        int requestCount = 0;
        server.OnRequest(req =>
        {
            requestCount++;
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildAnswer(query, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions { Forwarders = { udp } });

        var q = new DnsQuestion("example.com", DnsRecordType.A);
        DnsMessage a = await resolver.ResolveAsync(q);
        DnsMessage b = await resolver.ResolveAsync(q);

        Assert.Same(a, b); // Cache hit returns the exact same object.
        Assert.Equal(1, requestCount);
        Assert.Equal(1, resolver.CacheCount);
    }

    [Fact]
    public async Task ResolveAsync_NxDomainSurfacesAsNotFoundAndIsCached()
    {
        await using var server = new LoopbackUdpDnsServer();
        int requestCount = 0;
        server.OnRequest(req =>
        {
            requestCount++;
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildNxDomain(query, DnsTestMessages.ExampleComSoa());
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions { Forwarders = { udp } });

        var q = new DnsQuestion("nope.example.com", DnsRecordType.A);

        DnsException ex1 = await Assert.ThrowsAsync<DnsException>(() => resolver.ResolveAsync(q));
        Assert.Equal(DnsErrorCode.NotFound, ex1.Code);

        // Second call also throws NotFound but doesn't hit the transport — cached NXDOMAIN.
        DnsException ex2 = await Assert.ThrowsAsync<DnsException>(() => resolver.ResolveAsync(q));
        Assert.Equal(DnsErrorCode.NotFound, ex2.Code);
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task ResolveAsync_ServerFailureSurfacesAsServerFailure()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildWithRcode(query, DnsResponseCode.ServFail);
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions { Forwarders = { udp } });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.ServerFailure, ex.Code);
        Assert.Equal(DnsResponseCode.ServFail, ex.ResponseCode);
    }

    [Fact]
    public async Task ResolveAsync_TransactionIdMismatchSurfacesAsSpoofed()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            // Return a response with an intentionally wrong id.
            ushort wrongId = (ushort)(query.Header.Id ^ 0xFFFF);
            return DnsTestMessages.BuildWithCustomIdAndQuestion(
                wrongId,
                query.Questions[0],
                new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(1),
        });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.Spoofed, ex.Code);
    }

    [Fact]
    public async Task ResolveAsync_QuestionMismatchSurfacesAsSpoofed()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            // Echo correct ID but a different question.
            return DnsTestMessages.BuildWithCustomIdAndQuestion(
                query.Header.Id,
                new DnsQuestion("evil.example.com", DnsRecordType.A),
                new DnsRecord[] { new DnsARecord("evil.example.com", IPAddress.Parse("6.6.6.6"), 300) });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(1),
        });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.Spoofed, ex.Code);
    }

    [Fact]
    public async Task ResolveAsync_FailsOverToSecondForwarderOnTransportFailure()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildAnswer(query, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        // First forwarder points to TEST-NET-1 (RFC 5737) so connect will fail or time out.
        using var bad = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 53),
            QueryTimeout = TimeSpan.FromMilliseconds(200),
        });
        using var good = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(1),
        });

        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { bad, good },
            QueryTimeout = TimeSpan.FromSeconds(5),
        });

        DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.Single(answer.Answers);
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToTcpOnTcBit()
    {
        await using var udpServer = new LoopbackUdpDnsServer();
        await using var tcpServer = new LoopbackTcpDnsServer();

        int udpHits = 0, tcpHits = 0;
        udpServer.OnRequest(req =>
        {
            udpHits++;
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            // Truncated response with no answer records.
            return DnsTestMessages.BuildAnswer(query, Array.Empty<DnsRecord>(), truncated: true);
        });
        tcpServer.OnRequest(req =>
        {
            tcpHits++;
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildAnswer(query, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = udpServer.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(1),
        });
        using var tcp = new TcpDnsTransport(new TcpDnsTransportOptions
        {
            EndPoint = tcpServer.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(1),
        });

        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            TcpFallbacks = { [udp] = tcp },
            QueryTimeout = TimeSpan.FromSeconds(5),
        });

        DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.Single(answer.Answers);
        Assert.Equal(1, udpHits);
        Assert.Equal(1, tcpHits);
    }

    [Fact]
    public async Task ResolveAsync_TruncatedReturnedAsIsWhenNoTcpFallback()
    {
        await using var udpServer = new LoopbackUdpDnsServer();
        udpServer.OnRequest(req =>
        {
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildAnswer(query, Array.Empty<DnsRecord>(), truncated: true);
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = udpServer.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(1),
        });

        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

        DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.True((answer.Header.Flags & DnsHeaderFlags.Truncated) != 0);
    }

    [Fact]
    public async Task ResolveAsync_OutgoingQueryCarriesRdAndEdns()
    {
        DnsMessage? capturedQuery = null;
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            capturedQuery = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildAnswer(capturedQuery, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            EdnsPayloadSize = 1232,
        });

        _ = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.NotNull(capturedQuery);
        Assert.True((capturedQuery!.Header.Flags & DnsHeaderFlags.RecursionDesired) != 0);
        DnsOptRecord? opt = capturedQuery.Edns;
        Assert.NotNull(opt);
        Assert.Equal(1232, opt!.UdpPayloadSize);
    }

    [Fact]
    public async Task ResolveAsync_DisablingEdns_OmitsOptRecord()
    {
        DnsMessage? capturedQuery = null;
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            capturedQuery = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildAnswer(capturedQuery, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            EdnsPayloadSize = 0,
        });

        _ = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.Null(capturedQuery!.Edns);
    }

    [Fact]
    public async Task ClearCacheAsync_ForcesRefetch()
    {
        int hits = 0;
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            hits++;
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildAnswer(query, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions { Forwarders = { udp } });

        _ = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
        _ = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.Equal(1, hits);

        await resolver.ClearCacheAsync();
        _ = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.Equal(2, hits);
    }

    [Fact]
    public async Task ResolveAsync_DisablingCache_SkipsCacheEntirely()
    {
        int hits = 0;
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            hits++;
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildAnswer(query, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            EnableCache = false,
        });

        _ = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
        _ = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.Equal(2, hits);
        Assert.Equal(0, resolver.CacheCount);
    }

    [Fact]
    public async Task ResolveAsync_PropagatesExternalCancellation()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(_ => Array.Empty<byte>()); // black-hole

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(10),
        });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            QueryTimeout = TimeSpan.FromSeconds(10),
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A), cts.Token));
    }

    [Fact]
    public async Task ResolveAsync_QueryTimeoutSurfacesAsTimeout()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(_ => Array.Empty<byte>()); // black-hole

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(10),
        });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions
        {
            Forwarders = { udp },
            QueryTimeout = TimeSpan.FromMilliseconds(200),
        });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.Timeout, ex.Code);
    }

    [Fact]
    public void Constructor_RejectsEmptyForwarders()
    {
        Assert.Throws<ArgumentException>(
            () => new ForwardingDnsResolver(new ForwardingDnsResolverOptions()));
    }

    [Fact]
    public void Constructor_RejectsNullForwarder()
    {
        var options = new ForwardingDnsResolverOptions();
        options.Forwarders.Add(null!);
        Assert.Throws<ArgumentException>(() => new ForwardingDnsResolver(options));
    }

    [Fact]
    public async Task ResolveAsync_AfterDispose_ThrowsObjectDisposed()
    {
        await using var server = new LoopbackUdpDnsServer();
        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var resolver = new ForwardingDnsResolver(new ForwardingDnsResolverOptions { Forwarders = { udp } });

        resolver.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A)));
    }
}
