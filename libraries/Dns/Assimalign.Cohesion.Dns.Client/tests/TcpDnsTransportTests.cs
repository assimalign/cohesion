using System;
using System.Buffers.Binary;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Dns.Tests;

public sealed class TcpDnsTransportTests
{
    [Fact]
    public async Task ExchangeAsync_RoundTripsAgainstLoopbackServer()
    {
        await using var server = new LoopbackTcpDnsServer();
        server.OnRequest(req =>
        {
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(0, 2));
            return LoopbackUdpDnsServer.BuildMinimalResponse(id);
        });

        using var transport = new TcpDnsTransport(new TcpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            ConnectTimeout = TimeSpan.FromSeconds(1),
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

        byte[] request = LoopbackUdpDnsServer.BuildMinimalResponse(0xABCD);
        ReadOnlyMemory<byte> response = await transport.ExchangeAsync(request);

        Assert.Equal(12, response.Length);
        Assert.Equal(0xABCD, BinaryPrimitives.ReadUInt16BigEndian(response.Span[..2]));
    }

    [Fact]
    public async Task ExchangeAsync_ReusesConnectionWhenServerHoldsIt()
    {
        await using var server = new LoopbackTcpDnsServer
        {
            HoldConnection = true,
        };
        server.OnRequest(req =>
        {
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(0, 2));
            return LoopbackUdpDnsServer.BuildMinimalResponse(id);
        });

        using var transport = new TcpDnsTransport(new TcpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            IdleTimeout = TimeSpan.FromSeconds(10),
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

        for (int i = 0; i < 3; i++)
        {
            byte[] req = LoopbackUdpDnsServer.BuildMinimalResponse((ushort)(0x1000 + i));
            var resp = await transport.ExchangeAsync(req);
            Assert.Equal(0x1000 + i, BinaryPrimitives.ReadUInt16BigEndian(resp.Span[..2]));
        }

        // Give the server's accept loop time to record any extra connections (it should only
        // have one).
        await Task.Delay(50);
        Assert.Equal(1, server.AcceptedConnections);
    }

    [Fact]
    public async Task ExchangeAsync_ReopensAfterIdleTimeout()
    {
        await using var server = new LoopbackTcpDnsServer
        {
            HoldConnection = true,
        };
        server.OnRequest(req =>
        {
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(0, 2));
            return LoopbackUdpDnsServer.BuildMinimalResponse(id);
        });

        using var transport = new TcpDnsTransport(new TcpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            IdleTimeout = TimeSpan.FromMilliseconds(100),
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

        byte[] req1 = LoopbackUdpDnsServer.BuildMinimalResponse(0x0001);
        _ = await transport.ExchangeAsync(req1);

        await Task.Delay(250); // idle past 100ms

        byte[] req2 = LoopbackUdpDnsServer.BuildMinimalResponse(0x0002);
        _ = await transport.ExchangeAsync(req2);

        await Task.Delay(50);
        Assert.Equal(2, server.AcceptedConnections);
    }

    [Fact]
    public async Task ExchangeAsync_ReopensAfterPriorFailure()
    {
        // Server drops the connection after the first exchange. Next exchange should reopen.
        await using var server = new LoopbackTcpDnsServer
        {
            HoldConnection = false, // closes after one exchange
        };
        server.OnRequest(req =>
        {
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(0, 2));
            return LoopbackUdpDnsServer.BuildMinimalResponse(id);
        });

        using var transport = new TcpDnsTransport(new TcpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            IdleTimeout = TimeSpan.FromSeconds(30),
            QueryTimeout = TimeSpan.FromSeconds(2),
        });

        _ = await transport.ExchangeAsync(LoopbackUdpDnsServer.BuildMinimalResponse(0xAAAA));
        _ = await transport.ExchangeAsync(LoopbackUdpDnsServer.BuildMinimalResponse(0xBBBB));

        await Task.Delay(50);
        Assert.Equal(2, server.AcceptedConnections);
    }

    [Fact]
    public async Task ExchangeAsync_TimesOutWhenServerStaysSilent()
    {
        await using var server = new LoopbackTcpDnsServer();
        server.OnRequest(_ => Array.Empty<byte>()); // drop: read but don't respond

        using var transport = new TcpDnsTransport(new TcpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromMilliseconds(200),
        });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => transport.ExchangeAsync(LoopbackUdpDnsServer.BuildMinimalResponse(0xDEAD)));
        // The server drops by closing the socket after reading the request, so we surface as
        // Transport (closed before declared bytes read). If the timeout wins first we surface
        // as Timeout. Either is acceptable.
        Assert.True(
            ex.Code is DnsErrorCode.Transport or DnsErrorCode.Timeout,
            $"Expected Transport or Timeout, got {ex.Code}");
    }

    [Fact]
    public async Task ExchangeAsync_TimesOutOnUnreachableEndpoint()
    {
        // 192.0.2.0/24 (TEST-NET-1, RFC 5737) is documentation-only address space — no host
        // there to receive a connect.
        using var transport = new TcpDnsTransport(new TcpDnsTransportOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 53),
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            QueryTimeout = TimeSpan.FromSeconds(5),
        });

        DnsException ex = await Assert.ThrowsAsync<DnsException>(
            () => transport.ExchangeAsync(LoopbackUdpDnsServer.BuildMinimalResponse(0)));
        // Either the connect times out (Timeout) or the kernel rejects the route (Transport).
        Assert.True(
            ex.Code is DnsErrorCode.Timeout or DnsErrorCode.Transport,
            $"Expected Timeout or Transport, got {ex.Code}");
    }

    [Fact]
    public async Task ExchangeAsync_PropagatesExternalCancellation()
    {
        await using var server = new LoopbackTcpDnsServer
        {
            // Server reads the request but hangs on the response so the client's receive
            // blocks until cancellation fires.
            HoldConnection = true,
        };
        server.OnRequest(_ => Array.Empty<byte>());

        using var transport = new TcpDnsTransport(new TcpDnsTransportOptions
        {
            EndPoint = server.EndPoint,
            QueryTimeout = TimeSpan.FromSeconds(10),
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.ExchangeAsync(LoopbackUdpDnsServer.BuildMinimalResponse(0), cts.Token));
    }

    [Fact]
    public void Constructor_RequiresEndPoint()
    {
        Assert.Throws<ArgumentException>(
            () => new TcpDnsTransport(new TcpDnsTransportOptions()));
    }

    [Fact]
    public void Constructor_RequiresPositiveTimeouts()
    {
        Assert.Throws<ArgumentException>(
            () => new TcpDnsTransport(new TcpDnsTransportOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 53),
                ConnectTimeout = TimeSpan.Zero,
            }));

        Assert.Throws<ArgumentException>(
            () => new TcpDnsTransport(new TcpDnsTransportOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 53),
                QueryTimeout = TimeSpan.Zero,
            }));

        Assert.Throws<ArgumentException>(
            () => new TcpDnsTransport(new TcpDnsTransportOptions
            {
                EndPoint = new IPEndPoint(IPAddress.Loopback, 53),
                IdleTimeout = TimeSpan.Zero,
            }));
    }
}
