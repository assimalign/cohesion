using System;
using System.Net;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Forwarded.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Forwarded.Tests;

/// <summary>
/// Coverage for the feature-first read convention: the <c>Effective*</c> members
/// consult the <see cref="IHttpForwardedFeature"/> and fall back to the wire values
/// when no producer has attached one.
/// </summary>
public class HttpContextForwardedExtensionsTests
{
    private static readonly IPEndPoint WireRemote = new(IPAddress.Parse("192.0.2.10"), 52100);
    private static readonly IPEndPoint ForwardedRemote = new(IPAddress.Parse("203.0.113.9"), 4711);

    [Fact(DisplayName = "Cohesion Test [Http.Forwarded] - Effective*: Without a feature, every member should return the wire value")]
    public void EffectiveMembers_WithoutFeature_ShouldReturnWireValues()
    {
        // Arrange
        IHttpContext context = new StubHttpContext(HttpScheme.Https, new HttpHost("wire.example"), WireRemote);

        // Act / Assert
        context.EffectiveScheme.ShouldBe(HttpScheme.Https);
        context.EffectiveHost.Value.ShouldBe("wire.example");
        context.EffectiveRemoteEndPoint.ShouldBe(WireRemote);
        context.EffectiveRemoteIp.ShouldBe(WireRemote.Address);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forwarded] - Effective*: With a feature attached, every member should return the resolved value")]
    public void EffectiveMembers_WithFeature_ShouldReturnResolvedValues()
    {
        // Arrange
        IHttpContext context = new StubHttpContext(HttpScheme.Http, new HttpHost("internal"), WireRemote);
        context.Features.Set(new StubForwardedFeature
        {
            Scheme = HttpScheme.Https,
            Host = new HttpHost("public.example"),
            RemoteEndPoint = ForwardedRemote,
            TrustedHopCount = 1,
        });

        // Act / Assert
        context.EffectiveScheme.ShouldBe(HttpScheme.Https);
        context.EffectiveHost.Value.ShouldBe("public.example");
        context.EffectiveRemoteEndPoint.ShouldBe(ForwardedRemote);
        context.EffectiveRemoteIp.ShouldBe(ForwardedRemote.Address);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forwarded] - Effective*: A feature with a null endpoint should win over the wire endpoint, not fall through")]
    public void EffectiveMembers_FeatureWithNullEndpoint_ShouldReturnFeatureValue()
    {
        // Arrange — the feature is authoritative once attached: a null effective
        // endpoint (a transport that reported none) must not silently fall back to
        // the wire value.
        IHttpContext context = new StubHttpContext(HttpScheme.Http, new HttpHost("internal"), WireRemote);
        context.Features.Set(new StubForwardedFeature
        {
            Scheme = HttpScheme.Http,
            Host = new HttpHost("internal"),
            RemoteEndPoint = null,
        });

        // Act / Assert
        context.EffectiveRemoteEndPoint.ShouldBeNull();
        context.EffectiveRemoteIp.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forwarded] - Effective*: Reading the members should never install a feature")]
    public void EffectiveMembers_Read_ShouldBeSideEffectFree()
    {
        // Arrange
        IHttpContext context = new StubHttpContext(HttpScheme.Http, new HttpHost("wire.example"), WireRemote);

        // Act
        _ = context.EffectiveScheme;
        _ = context.EffectiveHost;
        _ = context.EffectiveRemoteEndPoint;
        _ = context.EffectiveRemoteIp;

        // Assert
        context.Features.Get<IHttpForwardedFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Forwarded] - Effective*: A null context should throw")]
    public void EffectiveMembers_NullContext_ShouldThrow()
    {
        // Arrange
        IHttpContext context = null!;

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => _ = context.EffectiveScheme);
        Should.Throw<ArgumentNullException>(() => _ = context.EffectiveHost);
        Should.Throw<ArgumentNullException>(() => _ = context.EffectiveRemoteEndPoint);
        Should.Throw<ArgumentNullException>(() => _ = context.EffectiveRemoteIp);
    }
}
