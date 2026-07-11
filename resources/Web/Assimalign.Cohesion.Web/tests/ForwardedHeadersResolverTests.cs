using System;
using System.Net;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Internal;
using Assimalign.Cohesion.Web.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Tests;

/// <summary>
/// Unit coverage for the trust-model walk: rightmost-first evaluation, the
/// KnownProxies/KnownNetworks/ForwardLimit boundary, spoofing rejection, malformed-hop
/// handling, and the RFC 7239 vs X-Forwarded-* precedence policy.
/// </summary>
public class ForwardedHeadersResolverTests
{
    private static readonly IPEndPoint LoopbackRemote = new(IPAddress.Loopback, 52100);
    private static readonly IPEndPoint UntrustedRemote = new(IPAddress.Parse("198.51.100.7"), 52100);

    private static ForwardedHeadersResolver CreateResolver(Action<ForwardedHeadersOptions>? configure = null)
    {
        var options = new ForwardedHeadersOptions
        {
            Headers = ForwardedHeaders.All,
            ForwardLimit = null,
        };
        configure?.Invoke(options);
        return new ForwardedHeadersResolver(options);
    }

    private static HttpHeaderCollection CreateHeaders(
        string? forwarded = null,
        string? xForwardedFor = null,
        string? xForwardedProto = null,
        string? xForwardedHost = null)
    {
        var headers = new HttpHeaderCollection();
        if (forwarded is not null)
        {
            headers[HttpHeaderKey.Forwarded] = forwarded;
        }
        if (xForwardedFor is not null)
        {
            headers[HttpHeaderKey.XForwardedFor] = xForwardedFor;
        }
        if (xForwardedProto is not null)
        {
            headers[HttpHeaderKey.XForwardedProto] = xForwardedProto;
        }
        if (xForwardedHost is not null)
        {
            headers[HttpHeaderKey.XForwardedHost] = xForwardedHost;
        }
        return headers;
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: A single X-Forwarded-For hop from a trusted peer should become the effective client")]
    public void Resolve_SingleHopFromTrustedPeer_ShouldApplyEntry()
    {
        // Arrange
        var resolver = CreateResolver();
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("internal"));

        // Assert
        feature.TrustedHopCount.ShouldBe(1);
        feature.RemoteIp.ShouldBe(IPAddress.Parse("203.0.113.9"));
        feature.RemotePort.ShouldBe(0);
        feature.OriginalRemoteEndPoint.ShouldBe(LoopbackRemote);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: Headers from an untrusted direct peer should be ignored entirely (spoofing defense)")]
    public void Resolve_UntrustedDirectPeer_ShouldIgnoreAllHeaders()
    {
        // Arrange — the direct peer is a public address outside every trust list, i.e. a
        // client talking straight to the server while asserting a forwarded chain.
        var resolver = CreateResolver();
        var headers = CreateHeaders(
            forwarded: "for=203.0.113.9;proto=https;host=spoofed.example",
            xForwardedFor: "203.0.113.9",
            xForwardedProto: "https",
            xForwardedHost: "spoofed.example");

        // Act
        var feature = resolver.Resolve(UntrustedRemote, headers, HttpScheme.Http, new HttpHost("real.example"));

        // Assert
        feature.TrustedHopCount.ShouldBe(0);
        feature.RemoteEndPoint.ShouldBe(UntrustedRemote);
        feature.Scheme.ShouldBe(HttpScheme.Http);
        feature.Host.Value.ShouldBe("real.example");
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: A null transport remote endpoint should never be trusted")]
    public void Resolve_NullRemoteEndPoint_ShouldIgnoreAllHeaders()
    {
        // Arrange
        var resolver = CreateResolver();
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9");

        // Act
        var feature = resolver.Resolve(null, headers, HttpScheme.Http, new HttpHost("real.example"));

        // Assert
        feature.TrustedHopCount.ShouldBe(0);
        feature.RemoteEndPoint.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: A trusted multi-hop chain should walk rightmost-first to the outermost client")]
    public void Resolve_TrustedChain_ShouldWalkToOutermostClient()
    {
        // Arrange — loopback hands over 10.0.0.2's entry, 10.0.0.2 (trusted via the
        // network) hands over the client's entry.
        var resolver = CreateResolver(o => o.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8")));
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9, 10.0.0.2");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert
        feature.TrustedHopCount.ShouldBe(2);
        feature.RemoteIp.ShouldBe(IPAddress.Parse("203.0.113.9"));
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: The walk should stop at the first untrusted intermediate hop")]
    public void Resolve_UntrustedIntermediateHop_ShouldStopWalk()
    {
        // Arrange — 203.0.113.50 is not in any trust list, so the entry it vouches for
        // (203.0.113.9, attacker-writable) must not apply.
        var resolver = CreateResolver();
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9, 203.0.113.50");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert — only the loopback-vouched entry applied.
        feature.TrustedHopCount.ShouldBe(1);
        feature.RemoteIp.ShouldBe(IPAddress.Parse("203.0.113.50"));
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: ForwardLimit should cap accepted hops at the nearest entries")]
    public void Resolve_ForwardLimit_ShouldCapAcceptedHops()
    {
        // Arrange — everything is trusted; the limit alone stops the walk.
        var resolver = CreateResolver(o =>
        {
            o.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8"));
            o.ForwardLimit = 2;
        });
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9, 10.0.0.3, 10.0.0.2");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert — two nearest entries accepted; the leftmost never evaluated.
        feature.TrustedHopCount.ShouldBe(2);
        feature.RemoteIp.ShouldBe(IPAddress.Parse("10.0.0.3"));
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: The default ForwardLimit should accept exactly one hop")]
    public void Resolve_DefaultForwardLimit_ShouldAcceptOneHop()
    {
        // Arrange — defaults: ForwardLimit = 1.
        var resolver = new ForwardedHeadersResolver(new ForwardedHeadersOptions
        {
            Headers = ForwardedHeaders.XForwardedFor,
        });
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9, 127.0.0.1");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert — even though 127.0.0.1 is itself trusted, the limit stops the walk.
        feature.TrustedHopCount.ShouldBe(1);
        feature.RemoteIp.ShouldBe(IPAddress.Loopback);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: A malformed X-Forwarded-For entry should stop the walk before applying that hop")]
    public void Resolve_MalformedEntryMidChain_ShouldStopBeforeApplyingIt()
    {
        // Arrange
        var resolver = CreateResolver(o => o.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8")));
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9, not-an-address, 10.0.0.2");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert — the nearest entry applied; the malformed one and everything beyond it did not.
        feature.TrustedHopCount.ShouldBe(1);
        feature.RemoteIp.ShouldBe(IPAddress.Parse("10.0.0.2"));
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: A malformed nearest X-Forwarded-For entry should resolve nothing")]
    public void Resolve_MalformedNearestEntry_ShouldResolveNothing()
    {
        // Arrange
        var resolver = CreateResolver();
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9, garbage$value");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert
        feature.TrustedHopCount.ShouldBe(0);
        feature.RemoteEndPoint.ShouldBe(LoopbackRemote);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: Bare and bracketed IPv6 entries should both resolve, brackets carrying the port")]
    public void Resolve_IPv6Entries_ShouldResolveWithAndWithoutBrackets()
    {
        // Arrange
        var resolver = CreateResolver();

        // Act — the de-facto bare spelling (no port can be carried).
        var bare = resolver.Resolve(LoopbackRemote, CreateHeaders(xForwardedFor: "2001:db8::1"), HttpScheme.Http, new HttpHost("h"));
        // Act — the bracketed spelling, which is how a port can be carried.
        var bracketed = resolver.Resolve(LoopbackRemote, CreateHeaders(xForwardedFor: "[2001:db8::1]:4711"), HttpScheme.Http, new HttpHost("h"));

        // Assert
        bare.RemoteIp.ShouldBe(IPAddress.Parse("2001:db8::1"));
        bare.RemotePort.ShouldBe(0);
        bracketed.RemoteIp.ShouldBe(IPAddress.Parse("2001:db8::1"));
        bracketed.RemotePort.ShouldBe(4711);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: IPv4-mapped IPv6 addresses should normalize for trust checks and results")]
    public void Resolve_IPv4MappedEntries_ShouldNormalizeToIPv4()
    {
        // Arrange — the intermediate hop arrives in mapped form but the trust network is
        // declared in native IPv4; the client entry is mapped too.
        var resolver = CreateResolver(o => o.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8")));
        var headers = CreateHeaders(xForwardedFor: "::ffff:203.0.113.9, ::ffff:10.0.0.2");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert
        feature.TrustedHopCount.ShouldBe(2);
        feature.RemoteIp.ShouldBe(IPAddress.Parse("203.0.113.9"));
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: An 'unknown' entry should count as a hop but stop the walk without changing the client")]
    public void Resolve_UnknownEntry_ShouldStopWalkWithoutAdvancingClient()
    {
        // Arrange
        var resolver = CreateResolver(o => o.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8")));
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9, unknown, 10.0.0.2");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert — 10.0.0.2 applied; 'unknown' was vouched for (counted) but carries no
        // address, so 203.0.113.9 is unreachable.
        feature.TrustedHopCount.ShouldBe(2);
        feature.RemoteIp.ShouldBe(IPAddress.Parse("10.0.0.2"));
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: Proto and host entries should apply per accepted hop, deepest accepted entry winning")]
    public void Resolve_ProtoAndHostChain_ShouldApplyDeepestAcceptedEntry()
    {
        // Arrange — two trusted hops; the deeper (client-side) values must win.
        var resolver = CreateResolver(o => o.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8")));
        var headers = CreateHeaders(
            xForwardedFor: "203.0.113.9, 10.0.0.2",
            xForwardedProto: "https, http",
            xForwardedHost: "public.example, edge.internal");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert
        feature.Scheme.ShouldBe(HttpScheme.Https);
        feature.Host.Value.ShouldBe("public.example");
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: Without an X-Forwarded-For chain, proto/host should apply at most one hop deep")]
    public void Resolve_ProtoWithoutForChain_ShouldApplySingleHopOnly()
    {
        // Arrange — no address chain exists, so even an unlimited ForwardLimit cannot
        // vouch for entries beyond the one appended by the direct peer.
        var resolver = CreateResolver(o => o.Headers = ForwardedHeaders.XForwardedProto);
        var headers = CreateHeaders(xForwardedProto: "https, http", xForwardedFor: "203.0.113.9");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert — rightmost proto applied; X-Forwarded-For ignored (not selected).
        feature.TrustedHopCount.ShouldBe(1);
        feature.Scheme.ShouldBe(HttpScheme.Http);
        feature.RemoteEndPoint.ShouldBe(LoopbackRemote);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: Asymmetric X-Forwarded-* lists should apply values only at depths where entries exist")]
    public void Resolve_AsymmetricLists_ShouldApplyValuesWhereEntriesExist()
    {
        // Arrange — two address hops but only one proto entry: the proto belongs to the
        // nearest hop and no deeper proto claim exists.
        var resolver = CreateResolver(o => o.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8")));
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9, 10.0.0.2", xForwardedProto: "https");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert
        feature.TrustedHopCount.ShouldBe(2);
        feature.RemoteIp.ShouldBe(IPAddress.Parse("203.0.113.9"));
        feature.Scheme.ShouldBe(HttpScheme.Https);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: An invalid proto value should stop the walk before applying the hop")]
    public void Resolve_InvalidProtoValue_ShouldStopBeforeApplyingHop()
    {
        // Arrange
        var resolver = CreateResolver();
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9", xForwardedProto: "gopher");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert — the whole hop is rejected, including its well-formed address entry.
        feature.TrustedHopCount.ShouldBe(0);
        feature.RemoteEndPoint.ShouldBe(LoopbackRemote);
        feature.Scheme.ShouldBe(HttpScheme.Http);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: An implausible host value should stop the walk before applying the hop")]
    public void Resolve_ImplausibleHostValue_ShouldStopBeforeApplyingHop()
    {
        // Arrange — a host smuggling a path separator must not reach downstream URL logic.
        var resolver = CreateResolver();
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9", xForwardedHost: "evil.example/path");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("safe.example"));

        // Assert
        feature.TrustedHopCount.ShouldBe(0);
        feature.Host.Value.ShouldBe("safe.example");
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: Only selected headers should be honored (header-name selection)")]
    public void Resolve_UnselectedHeaders_ShouldBeIgnored()
    {
        // Arrange — only X-Forwarded-Proto is honored; the present X-Forwarded-For and
        // X-Forwarded-Host must not influence the result.
        var resolver = CreateResolver(o => o.Headers = ForwardedHeaders.XForwardedProto);
        var headers = CreateHeaders(
            xForwardedFor: "203.0.113.9",
            xForwardedProto: "https",
            xForwardedHost: "other.example");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert
        feature.Scheme.ShouldBe(HttpScheme.Https);
        feature.RemoteEndPoint.ShouldBe(LoopbackRemote);
        feature.Host.Value.ShouldBe("h");
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: A trusted RFC 7239 element should apply for, proto, and host as one unit")]
    public void Resolve_ForwardedElement_ShouldApplyAllValues()
    {
        // Arrange
        var resolver = CreateResolver(o => o.Headers = ForwardedHeaders.Forwarded);
        var headers = CreateHeaders(forwarded: "for=\"[2001:db8::1]:4711\";proto=https;host=api.example.com");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("internal"));

        // Assert
        feature.TrustedHopCount.ShouldBe(1);
        feature.RemoteIp.ShouldBe(IPAddress.Parse("2001:db8::1"));
        feature.RemotePort.ShouldBe(4711);
        feature.Scheme.ShouldBe(HttpScheme.Https);
        feature.Host.Value.ShouldBe("api.example.com");
        feature.OriginalScheme.ShouldBe(HttpScheme.Http);
        feature.OriginalHost.Value.ShouldBe("internal");
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: An RFC 7239 chain should walk rightmost-first under the same trust boundary")]
    public void Resolve_ForwardedChain_ShouldWalkRightmostFirst()
    {
        // Arrange
        var resolver = CreateResolver(o =>
        {
            o.Headers = ForwardedHeaders.Forwarded;
            o.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8"));
        });
        var headers = CreateHeaders(forwarded: "for=203.0.113.9;proto=https, for=10.0.0.2");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert
        feature.TrustedHopCount.ShouldBe(2);
        feature.RemoteIp.ShouldBe(IPAddress.Parse("203.0.113.9"));
        feature.Scheme.ShouldBe(HttpScheme.Https);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: An obfuscated RFC 7239 'for' should apply the hop's other values and stop the walk")]
    public void Resolve_ObfuscatedForwardedNode_ShouldApplyValuesAndStop()
    {
        // Arrange — the proxy hides the upstream identity but still asserts the scheme.
        var resolver = CreateResolver(o =>
        {
            o.Headers = ForwardedHeaders.Forwarded;
            o.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8"));
        });
        var headers = CreateHeaders(forwarded: "for=203.0.113.9, for=_hidden;proto=https");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert — proto applied from the vouched hop; the effective client stays at the
        // transport peer because no address was disclosed, and the deeper entry is
        // unreachable without one.
        feature.TrustedHopCount.ShouldBe(1);
        feature.Scheme.ShouldBe(HttpScheme.Https);
        feature.RemoteEndPoint.ShouldBe(LoopbackRemote);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: A present-but-malformed Forwarded header should resolve nothing and never fall back")]
    public void Resolve_MalformedForwardedHeader_ShouldPoisonResolution()
    {
        // Arrange — both families selected and present; the RFC header is malformed. A
        // fallback to the legacy family would let a malformed header buy the attacker a
        // different evaluation path, so nothing may resolve.
        var resolver = CreateResolver();
        var headers = CreateHeaders(forwarded: "for=203.0.113.9;=broken", xForwardedFor: "203.0.113.77");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert
        feature.TrustedHopCount.ShouldBe(0);
        feature.RemoteEndPoint.ShouldBe(LoopbackRemote);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: When both families are present and selected, RFC 7239 should win")]
    public void Resolve_BothFamiliesPresent_ShouldPreferForwarded()
    {
        // Arrange
        var resolver = CreateResolver();
        var headers = CreateHeaders(forwarded: "for=203.0.113.9", xForwardedFor: "203.0.113.77");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert — the RFC value, not the legacy one.
        feature.RemoteIp.ShouldBe(IPAddress.Parse("203.0.113.9"));
        feature.TrustedHopCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: An unselected Forwarded header should leave the legacy family in charge")]
    public void Resolve_ForwardedNotSelected_ShouldUseLegacyFamily()
    {
        // Arrange
        var resolver = CreateResolver(o => o.Headers = ForwardedHeaders.XForwarded);
        var headers = CreateHeaders(forwarded: "for=203.0.113.9", xForwardedFor: "203.0.113.77");

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert
        feature.RemoteIp.ShouldBe(IPAddress.Parse("203.0.113.77"));
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: A non-IP transport peer should be trusted only while TrustLocalTransports is set")]
    public void Resolve_NonIpTransportPeer_ShouldHonorTrustLocalTransports()
    {
        // Arrange — the same exchange over a machine-local (non-IP) transport endpoint.
        var localRemote = new FakeLocalEndPoint();
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9");
        var trusting = CreateResolver();
        var hardened = CreateResolver(o => o.TrustLocalTransports = false);

        // Act
        var trusted = trusting.Resolve(localRemote, headers, HttpScheme.Http, new HttpHost("h"));
        var ignored = hardened.Resolve(localRemote, headers, HttpScheme.Http, new HttpHost("h"));

        // Assert
        trusted.TrustedHopCount.ShouldBe(1);
        trusted.RemoteIp.ShouldBe(IPAddress.Parse("203.0.113.9"));
        ignored.TrustedHopCount.ShouldBe(0);
        ignored.RemoteEndPoint.ShouldBe(localRemote);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: A DnsEndPoint peer should never be trusted")]
    public void Resolve_DnsEndPointPeer_ShouldNeverBeTrusted()
    {
        // Arrange — a DnsEndPoint neither names an IP nor proves a machine-local transport.
        var resolver = CreateResolver();
        var headers = CreateHeaders(xForwardedFor: "203.0.113.9");

        // Act
        var feature = resolver.Resolve(new DnsEndPoint("proxy.example", 443), headers, HttpScheme.Http, new HttpHost("h"));

        // Assert
        feature.TrustedHopCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolve: No forwarding headers should yield the wire values with zero hops")]
    public void Resolve_NoHeaders_ShouldReturnWireValues()
    {
        // Arrange
        var resolver = CreateResolver();
        var headers = CreateHeaders();

        // Act
        var feature = resolver.Resolve(LoopbackRemote, headers, HttpScheme.Https, new HttpHost("wire.example"));

        // Assert — the feature still answers, with effective == original.
        feature.TrustedHopCount.ShouldBe(0);
        feature.Scheme.ShouldBe(HttpScheme.Https);
        feature.Host.Value.ShouldBe("wire.example");
        feature.RemoteEndPoint.ShouldBe(LoopbackRemote);
        feature.OriginalScheme.ShouldBe(HttpScheme.Https);
        feature.OriginalHost.Value.ShouldBe("wire.example");
        feature.OriginalRemoteEndPoint.ShouldBe(LoopbackRemote);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolver: Options should be snapshotted at construction, not read per request")]
    public void Resolver_OptionsMutatedAfterConstruction_ShouldNotAffectResolution()
    {
        // Arrange
        var options = new ForwardedHeadersOptions { Headers = ForwardedHeaders.XForwardedFor, ForwardLimit = null };
        var resolver = new ForwardedHeadersResolver(options);

        // Act — widen the trust boundary after the snapshot was taken.
        options.KnownProxies.Add(IPAddress.Parse("198.51.100.7"));
        options.Headers = ForwardedHeaders.None;
        var feature = resolver.Resolve(UntrustedRemote, CreateHeaders(xForwardedFor: "203.0.113.9"), HttpScheme.Http, new HttpHost("h"));

        // Assert — the late-added proxy is not trusted by the composed resolver.
        feature.TrustedHopCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolver: Construction should reject an empty header selection")]
    public void Resolver_HeadersNone_ShouldThrow()
    {
        // Arrange
        var options = new ForwardedHeadersOptions();

        // Act / Assert
        Should.Throw<ArgumentException>(() => new ForwardedHeadersResolver(options));
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolver: Construction should reject a ForwardLimit below one")]
    public void Resolver_ForwardLimitBelowOne_ShouldThrow()
    {
        // Arrange
        var options = new ForwardedHeadersOptions { Headers = ForwardedHeaders.All, ForwardLimit = 0 };

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => new ForwardedHeadersResolver(options));
    }

    [Fact(DisplayName = "Cohesion Test [Web] - Resolver: Construction should reject a null KnownProxies entry")]
    public void Resolver_NullKnownProxyEntry_ShouldThrow()
    {
        // Arrange
        var options = new ForwardedHeadersOptions { Headers = ForwardedHeaders.All };
        options.KnownProxies.Add(null!);

        // Act / Assert
        Should.Throw<ArgumentException>(() => new ForwardedHeadersResolver(options));
    }
}
