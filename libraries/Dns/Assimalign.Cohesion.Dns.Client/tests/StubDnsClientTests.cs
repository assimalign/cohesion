using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns.Tests;

public sealed class StubDnsClientTests
{
    [Fact]
    public async Task QueryAsync_RoundTripsAgainstLoopbackServer()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildAnswer(query, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var stub = new StubDnsClient(new StubDnsClientOptions { Transport = udp });

        DnsMessage answer = await stub.QueryAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.Single(answer.Answers);
        Assert.IsType<DnsARecord>(answer.Answers[0]);
    }

    [Fact]
    public async Task QueryAsync_NxDomainSurfacesAsNotFound()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildNxDomain(query, DnsTestMessages.ExampleComSoa());
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var stub = new StubDnsClient(new StubDnsClientOptions { Transport = udp });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => stub.QueryAsync(new DnsQuestion("nope.example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task QueryAsync_ServerFailureSurfacesAsServerFailure()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            DnsMessage query = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildWithRcode(query, DnsResponseCode.ServFail);
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var stub = new StubDnsClient(new StubDnsClientOptions { Transport = udp });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => stub.QueryAsync(new DnsQuestion("example.com", DnsRecordType.A)));
        Assert.Equal(DnsErrorCode.ServerFailure, ex.Code);
        Assert.Equal(DnsResponseCode.ServFail, ex.ResponseCode);
    }

    [Fact]
    public async Task QueryAsync_NoOptWhenEdnsDisabled()
    {
        DnsMessage? captured = null;
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            captured = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildAnswer(captured, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var stub = new StubDnsClient(new StubDnsClientOptions { Transport = udp, EdnsPayloadSize = 0 });

        _ = await stub.QueryAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.Null(captured!.Edns);
    }

    [Fact]
    public async Task QueryAsync_RecursionDesiredCleared_WhenConfigured()
    {
        DnsMessage? captured = null;
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            captured = DnsTestMessages.ParseQuery(req);
            return DnsTestMessages.BuildAnswer(captured, new DnsRecord[] { DnsTestMessages.ExampleComA() });
        });

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions { EndPoint = server.EndPoint });
        var stub = new StubDnsClient(new StubDnsClientOptions
        {
            Transport = udp,
            RecursionDesired = false,
        });

        _ = await stub.QueryAsync(new DnsQuestion("example.com", DnsRecordType.A));
        Assert.True((captured!.Header.Flags & DnsHeaderFlags.RecursionDesired) == 0);
    }

    [Fact]
    public void Constructor_RequiresTransport()
    {
        Assert.Throws<ArgumentException>(
            () => new StubDnsClient(new StubDnsClientOptions()));
    }

    [Fact]
    public async Task QueryAsync_PropagatesExternalCancellation()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(_ => Array.Empty<byte>());

        using var udp = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(10),
        });
        var stub = new StubDnsClient(new StubDnsClientOptions
        {
            Transport = udp,
            QueryTimeout = TimeSpan.FromSeconds(10),
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stub.QueryAsync(new DnsQuestion("example.com", DnsRecordType.A), cts.Token));
    }
}
