using System;
using System.Buffers.Binary;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns.Tests;

public sealed class UdpDnsTransportTests
{
    [Fact]
    public async Task ExchangeAsync_BouncesRequestAgainstLoopbackServer()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            // Echo the request's transaction ID into a minimal NoError response.
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(0, 2));
            return LoopbackUdpDnsServer.BuildMinimalResponse(id);
        });

        using var transport = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

        byte[] request = LoopbackUdpDnsServer.BuildMinimalResponse(id: 0x1234);
        // The minimal-response builder happens to produce a valid 12-octet header; reuse it as
        // a stand-in request for the transport test.

        ReadOnlyMemory<byte> response = await transport.ExchangeAsync(request);
        Assert.Equal(12, response.Length);
        ushort echoedId = BinaryPrimitives.ReadUInt16BigEndian(response.Span[..2]);
        Assert.Equal(0x1234, echoedId);
    }

    [Fact]
    public async Task ExchangeAsync_TimesOutWhenServerDrops()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(_ => Array.Empty<byte>()); // black-hole every request

        using var transport = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromMilliseconds(200),
        });

        byte[] request = LoopbackUdpDnsServer.BuildMinimalResponse(0x4242);

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => transport.ExchangeAsync(request));
        Assert.Equal(DnsErrorCode.Timeout, ex.Code);
    }

    [Fact]
    public async Task ExchangeAsync_PropagatesExternalCancellation()
    {
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(_ => Array.Empty<byte>()); // black-hole

        using var transport = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(10),
        });

        byte[] request = LoopbackUdpDnsServer.BuildMinimalResponse(0xFFFF);
        using var externalCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // External cancellation surfaces as OperationCanceledException, not DnsException —
        // the resolver layer needs to see the cancellation cause directly.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.ExchangeAsync(request, externalCts.Token));
    }

    [Fact]
    public void Constructor_RequiresEndPoint()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new UdpDnsTransport(new UdpDnsTransportOptions()));
        Assert.Contains("EndPoint", ex.Message);
    }

    [Fact]
    public async Task ExchangeAsync_AfterDispose_ThrowsObjectDisposed()
    {
        await using var server = new LoopbackUdpDnsServer();
        var transport = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(1),
        });
        transport.Dispose();

        byte[] request = LoopbackUdpDnsServer.BuildMinimalResponse(0);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => transport.ExchangeAsync(request));
    }

    [Fact]
    public async Task ExchangeAsync_ConcurrentCalls_AreSerializedCorrectly()
    {
        // Two parallel exchanges against the same transport. Each gets the right echoed ID,
        // proving the per-exchange receive buffer isolation works.
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(req =>
        {
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(0, 2));
            // Slow the server a touch so the two exchanges genuinely race.
            Thread.Sleep(10);
            return LoopbackUdpDnsServer.BuildMinimalResponse(id);
        });

        using var transport = new UdpDnsTransport(new UdpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

        byte[] req1 = LoopbackUdpDnsServer.BuildMinimalResponse(0x1111);
        byte[] req2 = LoopbackUdpDnsServer.BuildMinimalResponse(0x2222);

        Task<ReadOnlyMemory<byte>> t1 = transport.ExchangeAsync(req1);
        Task<ReadOnlyMemory<byte>> t2 = transport.ExchangeAsync(req2);

        ReadOnlyMemory<byte>[] both = await Task.WhenAll(t1, t2);
        ushort id1 = BinaryPrimitives.ReadUInt16BigEndian(both[0].Span[..2]);
        ushort id2 = BinaryPrimitives.ReadUInt16BigEndian(both[1].Span[..2]);
        Assert.Equal(0x1111, id1);
        Assert.Equal(0x2222, id2);
    }
}
