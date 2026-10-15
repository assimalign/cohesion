using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Dns.Tests;

public sealed class DnsOptionsSnapshotTests
{
    [Theory(DisplayName = "Cohesion Test [Dns] - ResolveAsync: Iterative options are captured at construction")]
    [InlineData("MaxReferralDepth")]
    [InlineData("MaxQueriesPerResolve")]
    [InlineData("QueryTimeout")]
    [InlineData("UdpTransportFactory")]
    [InlineData("EnableQNameMinimization")]
    [InlineData("EdnsPayloadSize")]
    [InlineData("RootEndpoint")]
    public async Task ResolveAsync_IterativeOptionsMutated_UsesConstructionValues(string property)
    {
        // Arrange
        DnsMessage? captured = null;
        var endpoint = new IPEndPoint(IPAddress.Loopback, 53);
        IPEndPoint? capturedEndpoint = null;
        var options = new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { endpoint },
            EnableQNameMinimization = false,
            EnableCache = false,
            UdpTransportFactory = target =>
            {
                capturedEndpoint = target;
                return new SnapshotDnsTransport(query =>
                {
                    captured = query;
                    return DnsTestMessages.BuildAnswer(query, new[] { DnsTestMessages.ExampleComA() }, authoritative: true);
                });
            },
        };
        using var resolver = new IterativeDnsResolver(options);
        switch (property)
        {
            case "MaxReferralDepth": options.MaxReferralDepth = 0; break;
            case "MaxQueriesPerResolve": options.MaxQueriesPerResolve = 0; break;
            case "QueryTimeout": options.QueryTimeout = TimeSpan.FromMilliseconds(-2); break;
            case "UdpTransportFactory": options.UdpTransportFactory = null!; break;
            case "EnableQNameMinimization": options.EnableQNameMinimization = true; break;
            case "EdnsPayloadSize": options.EdnsPayloadSize = 0; break;
            case "RootEndpoint": endpoint.Port = 0; break;
        }

        // Act
        DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A), CancellationToken.None);

        // Assert
        answer.Answers.Count.ShouldBe(1);
        captured.ShouldNotBeNull();
        captured.Questions[0].ShouldBe(new DnsQuestion("example.com", DnsRecordType.A));
        captured.Edns.ShouldNotBeNull();
        capturedEndpoint.ShouldNotBeNull();
        capturedEndpoint.Port.ShouldBe(53);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - ResolveAsync: Negative later NS depth does not suppress sub-resolution")]
    public async Task ResolveAsync_NsResolutionDepthMutated_ResolvesOutOfBailiwickNameServer()
    {
        // Arrange
        await using var root = new LoopbackDnsAuthority(".");
        await using var com = new LoopbackDnsAuthority("com");
        await using var net = new LoopbackDnsAuthority("net");
        await using var dnsNet = new LoopbackDnsAuthority("example-dns.net");
        await using var example = new LoopbackDnsAuthority("example.com");
        root.Delegate("com", "ns1.com", com);
        root.Delegate("net", "ns1.net", net);
        net.Delegate("example-dns.net", "ns1.example-dns.net", dnsNet);
        dnsNet.AddRecord(new DnsARecord("ns1.example-dns.net", example.VirtualEndPoint.Address, 300));
        com.Delegate("example.com", "ns1.example-dns.net", example, includeGlue: false);
        example.AddRecord(DnsTestMessages.ExampleComA());
        var options = new IterativeDnsResolverOptions
        {
            RootEndpoints = new List<IPEndPoint> { root.VirtualEndPoint },
            UdpTransportFactory = LoopbackDnsAuthority.CreateTransportFactory(),
            TcpTransportFactory = null,
            EnableQNameMinimization = false,
            MaxNsResolutionDepth = 1,
        };
        using var resolver = new IterativeDnsResolver(options);
        options.MaxNsResolutionDepth = -1;

        // Act
        DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A), CancellationToken.None);

        // Assert
        answer.Answers.Count.ShouldBe(1);
        answer.Answers[0].ShouldBeOfType<DnsARecord>().Address.ShouldBe(IPAddress.Parse("93.184.216.34"));
        dnsNet.RequestCount.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Dns] - ResolveAsync: Iterative TCP fallback factory is captured at construction")]
    public async Task ResolveAsync_IterativeTcpFactoryCleared_UsesConstructionFallback()
    {
        // Arrange
        var options = new IterativeDnsResolverOptions
        {
            EnableQNameMinimization = false,
            UdpTransportFactory = _ => new SnapshotDnsTransport(query => DnsTestMessages.BuildAnswer(query, Array.Empty<DnsRecord>(), truncated: true)),
            TcpTransportFactory = _ => new SnapshotDnsTransport(query => DnsTestMessages.BuildAnswer(query, new[] { DnsTestMessages.ExampleComA() }, authoritative: true)),
        };
        using var resolver = new IterativeDnsResolver(options);
        options.TcpTransportFactory = null;

        // Act
        DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A), CancellationToken.None);

        // Assert
        answer.Answers.Count.ShouldBe(1);
        (answer.Header.Flags & DnsHeaderFlags.Truncated).ShouldBe(DnsHeaderFlags.None);
    }

    [Theory(DisplayName = "Cohesion Test [Dns] - QueryAsync: Stub options are captured at construction")]
    [InlineData("QueryTimeout")]
    [InlineData("Transport")]
    [InlineData("RecursionDesired")]
    [InlineData("EdnsPayloadSize")]
    public async Task QueryAsync_StubOptionsMutated_UsesConstructionValues(string property)
    {
        // Arrange
        DnsMessage? captured = null;
        using var transport = new SnapshotDnsTransport(query =>
        {
            captured = query;
            return DnsTestMessages.BuildAnswer(query, new[] { DnsTestMessages.ExampleComA() });
        });
        var options = new StubDnsClientOptions { Transport = transport };
        using var client = new StubDnsClient(options);
        switch (property)
        {
            case "QueryTimeout": options.QueryTimeout = TimeSpan.FromMilliseconds(-2); break;
            case "Transport": options.Transport = null; break;
            case "RecursionDesired": options.RecursionDesired = false; break;
            case "EdnsPayloadSize": options.EdnsPayloadSize = 0; break;
        }

        // Act
        DnsMessage answer = await client.QueryAsync(new DnsQuestion("example.com", DnsRecordType.A), CancellationToken.None);

        // Assert
        answer.Answers.Count.ShouldBe(1);
        captured.ShouldNotBeNull();
        (captured.Header.Flags & DnsHeaderFlags.RecursionDesired).ShouldBe(DnsHeaderFlags.RecursionDesired);
        captured.Edns.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Dns] - ResolveAsync: Forwarding options and collections are captured at construction")]
    [InlineData("QueryTimeout")]
    [InlineData("Forwarders")]
    [InlineData("TcpFallbacks")]
    [InlineData("EdnsPayloadSize")]
    public async Task ResolveAsync_ForwardingOptionsMutated_UsesConstructionValues(string property)
    {
        // Arrange
        DnsMessage? captured = null;
        using var udp = new SnapshotDnsTransport(query => DnsTestMessages.BuildAnswer(query, Array.Empty<DnsRecord>(), truncated: true));
        using var tcp = new SnapshotDnsTransport(query =>
        {
            captured = query;
            return DnsTestMessages.BuildAnswer(query, new[] { DnsTestMessages.ExampleComA() });
        });
        var options = new ForwardingDnsResolverOptions { Forwarders = { udp }, TcpFallbacks = { [udp] = tcp } };
        using var resolver = new ForwardingDnsResolver(options);
        switch (property)
        {
            case "QueryTimeout": options.QueryTimeout = TimeSpan.FromMilliseconds(-2); break;
            case "Forwarders": options.Forwarders.Clear(); break;
            case "TcpFallbacks": options.TcpFallbacks.Clear(); break;
            case "EdnsPayloadSize": options.EdnsPayloadSize = 0; break;
        }

        // Act
        DnsMessage answer = await resolver.ResolveAsync(new DnsQuestion("example.com", DnsRecordType.A), CancellationToken.None);

        // Assert
        answer.Answers.Count.ShouldBe(1);
        captured.ShouldNotBeNull();
        captured.Edns.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Dns] - ExchangeAsync: UDP options are captured at construction")]
    [InlineData("MaxResponseSize")]
    [InlineData("QueryTimeout")]
    [InlineData("EndPoint")]
    [InlineData("EndpointPort")]
    public async Task ExchangeAsync_UdpOptionsMutated_UsesConstructionValues(string property)
    {
        // Arrange
        await using var server = new LoopbackUdpDnsServer();
        server.OnRequest(request => request);
        var endpoint = new IPEndPoint(server.EndPoint.Address, server.EndPoint.Port);
        var options = new UdpDnsTransportOptions { EndPoint = endpoint, MaxResponseSize = 12 };
        using var transport = new UdpDnsTransport(options);
        switch (property)
        {
            case "MaxResponseSize": options.MaxResponseSize = -1; break;
            case "QueryTimeout": options.QueryTimeout = TimeSpan.FromMilliseconds(-2); break;
            case "EndPoint": options.EndPoint = null; break;
            case "EndpointPort": endpoint.Port = 0; break;
        }
        byte[] request = LoopbackUdpDnsServer.BuildMinimalResponse(0x1234);

        // Act
        ReadOnlyMemory<byte> response = await transport.ExchangeAsync(request, CancellationToken.None);

        // Assert
        response.ToArray().ShouldBe(request);
        transport.Endpoint.ShouldBe(server.EndPoint);
    }

    [Theory(DisplayName = "Cohesion Test [Dns] - ExchangeAsync: TCP options are captured at construction")]
    [InlineData("ConnectTimeout")]
    [InlineData("QueryTimeout")]
    [InlineData("IdleTimeout")]
    [InlineData("EndPoint")]
    [InlineData("EndpointPort")]
    public async Task ExchangeAsync_TcpOptionsMutated_UsesConstructionValues(string property)
    {
        // Arrange
        await using var server = new LoopbackTcpDnsServer { HoldConnection = true };
        server.OnRequest(request => request);
        var endpoint = new IPEndPoint(server.EndPoint.Address, server.EndPoint.Port);
        var options = new TcpDnsTransportOptions { EndPoint = endpoint };
        using var transport = new TcpDnsTransport(options);
        switch (property)
        {
            case "ConnectTimeout": options.ConnectTimeout = TimeSpan.FromMilliseconds(-2); break;
            case "QueryTimeout": options.QueryTimeout = TimeSpan.FromMilliseconds(-2); break;
            case "IdleTimeout": options.IdleTimeout = TimeSpan.FromMilliseconds(-2); break;
            case "EndPoint": options.EndPoint = null; break;
            case "EndpointPort": endpoint.Port = 0; break;
        }
        byte[] request = LoopbackUdpDnsServer.BuildMinimalResponse(0x1234);

        // Act
        ReadOnlyMemory<byte> first = await transport.ExchangeAsync(request, CancellationToken.None);
        ReadOnlyMemory<byte> second = await transport.ExchangeAsync(request, CancellationToken.None);

        // Assert
        first.ToArray().ShouldBe(request);
        second.ToArray().ShouldBe(request);
        server.AcceptedConnections.ShouldBe(1);
        transport.Endpoint.ShouldBe(server.EndPoint);
    }
}
