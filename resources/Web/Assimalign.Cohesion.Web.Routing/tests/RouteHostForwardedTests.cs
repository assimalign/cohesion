using System.Net;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Assimalign.Cohesion.Web.Routing.Tests.TestObjects;

using Shouldly;
using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Routing.Tests;

/// <summary>
/// Proxy-awareness coverage for host-constrained routes (#1077): <c>RequireHost</c> matches the effective
/// host. The pipeline cases run the real forwarded-headers middleware ahead of <c>UseRouting</c> over
/// <see cref="TestWebApplication"/>, with requests from a known proxy address that rewrote <c>Host</c> to
/// its upstream name and forwarded the client's host in <c>X-Forwarded-Host</c>. The router cases attach
/// an <see cref="IHttpForwardedFeature"/> directly.
/// </summary>
public class RouteHostForwardedTests
{
    private static readonly IPEndPoint _trustedProxy = new(IPAddress.Parse("10.0.0.2"), 51000);
    private static readonly IPEndPoint _untrustedPeer = new(IPAddress.Parse("198.51.100.7"), 52000);

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Forwarded: RequireHost should match the host a trusted proxy forwarded, not the upstream name it dialed")]
    public async Task UseRouting_TrustedProxyForwardsHost_ShouldMatchForwardedHost()
    {
        // Arrange
        RecordingRouterRouteHandler api = new();
        TestWebApplication app = CreateApplication(useForwardedHeaders: true);
        app.UseRouting().Map(HttpMethod.Get, "/data", api).RequireHost("api.example.com");
        TestHttpContext context = CreateRequest(_trustedProxy, "/data", wireHost: "internal.upstream:8080", forwardedHost: "api.example.com");

        // Act
        await app.ExecuteAsync(context);

        // Assert
        api.WasInvoked.ShouldBeTrue();
        context.GetRouteMatch().ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Forwarded: A route constrained to the internal host should not match a public request the proxy rewrote to that host")]
    public async Task UseRouting_TrustedProxyRewritesHostToInternalName_ShouldNotMatchInternalRoute()
    {
        // Arrange — the admin-on-internal-host pattern: the proxy dials the upstream by its internal name,
        // so every public request arrives with that wire Host.
        RecordingRouterRouteHandler admin = new();
        RecordingRouterRouteHandler site = new();
        TestWebApplication app = CreateApplication(useForwardedHeaders: true);
        IRouterBuilder routes = app.UseRouting();
        routes.Map(HttpMethod.Get, "/admin", admin).RequireHost("admin.internal");
        routes.Map(HttpMethod.Get, "/admin", site);
        TestHttpContext context = CreateRequest(_trustedProxy, "/admin", wireHost: "admin.internal", forwardedHost: "www.example.com");

        // Act
        await app.ExecuteAsync(context);

        // Assert — the public request falls through to the open route; the internal one never runs.
        admin.WasInvoked.ShouldBeFalse();
        site.WasInvoked.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Forwarded: Without UseForwardedHeaders RequireHost should match the wire host and ignore X-Forwarded-Host")]
    public async Task UseRouting_WithoutForwardedHeaders_ShouldMatchWireHost()
    {
        // Arrange — nothing resolves the forwarded host, so the header is attacker input, not identity.
        RecordingRouterRouteHandler api = new();
        RecordingRouterRouteHandler admin = new();
        TestWebApplication app = CreateApplication(useForwardedHeaders: false);
        IRouterBuilder routes = app.UseRouting();
        routes.Map(HttpMethod.Get, "/data", api).RequireHost("api.example.com");
        routes.Map(HttpMethod.Get, "/admin", admin).RequireHost("admin.internal");
        TestHttpContext wireMatches = CreateRequest(_trustedProxy, "/data", wireHost: "api.example.com", forwardedHost: "other.example.com");
        TestHttpContext spoofed = CreateRequest(_trustedProxy, "/admin", wireHost: "www.example.com", forwardedHost: "admin.internal");

        // Act
        await app.ExecuteAsync(wireMatches);
        await app.ExecuteAsync(spoofed);

        // Assert
        api.WasInvoked.ShouldBeTrue();
        admin.WasInvoked.ShouldBeFalse();
        spoofed.GetRouteMatch().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Forwarded: RequireHost should match the wire host when the peer that forwarded the host is not trusted")]
    public async Task UseRouting_UntrustedPeerForwardsHost_ShouldMatchWireHost()
    {
        // Arrange
        RecordingRouterRouteHandler admin = new();
        RecordingRouterRouteHandler api = new();
        TestWebApplication app = CreateApplication(useForwardedHeaders: true);
        IRouterBuilder routes = app.UseRouting();
        routes.Map(HttpMethod.Get, "/admin", admin).RequireHost("admin.internal");
        routes.Map(HttpMethod.Get, "/data", api).RequireHost("api.example.com");
        TestHttpContext spoofed = CreateRequest(_untrustedPeer, "/admin", wireHost: "www.example.com", forwardedHost: "admin.internal");
        TestHttpContext wireMatches = CreateRequest(_untrustedPeer, "/data", wireHost: "api.example.com", forwardedHost: "other.example.com");

        // Act
        await app.ExecuteAsync(spoofed);
        await app.ExecuteAsync(wireMatches);

        // Assert
        admin.WasInvoked.ShouldBeFalse();
        api.WasInvoked.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Forwarded: Match should compare host constraints, ports included, with the forwarded feature's host")]
    public void Match_ForwardedFeature_ShouldCompareConstraintsWithForwardedHostAndPort()
    {
        // Arrange — the wire authority is the proxy's upstream; the feature carries the client's.
        Route forwardedPort = CreateRoute("/data", "api.example.com:8443");
        Route wirePort = CreateRoute("/data", "*:8080");
        Router router = new(new IRouterRoute[] { wirePort, forwardedPort });
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/data");
        context.Request.Host = new HttpHost("internal.upstream:8080");
        context.Features.Set<IHttpForwardedFeature>(new ForwardedFeature(new HttpHost("api.example.com:8443"), context.Request.Host));

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.Matched);
        match.Route.ShouldBeSameAs(forwardedPort);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Forwarded: A route skipped on the forwarded host should not contribute to a 405")]
    public void Match_ForwardedHostMissesConstraint_ShouldNotReportMethodNotAllowed()
    {
        // Arrange — the wire host would satisfy the POST route and turn the GET into a 405.
        Route internalOnly = CreateRoute("/data", "internal.upstream", HttpMethod.Post);
        Router router = new(internalOnly);
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, "/data");
        context.Request.Host = new HttpHost("internal.upstream");
        context.Features.Set<IHttpForwardedFeature>(new ForwardedFeature(new HttpHost("www.example.com"), context.Request.Host));

        // Act
        RouteMatch match = router.Match(context);

        // Assert
        match.Status.ShouldBe(RouteMatchStatus.NoMatch);
    }

    private static TestWebApplication CreateApplication(bool useForwardedHeaders)
    {
        TestWebApplication app = new();
        app.AddRouting();

        if (useForwardedHeaders)
        {
            app.UseForwardedHeaders(options =>
            {
                options.Headers = ForwardedHeaderNames.XForwarded;
                options.KnownProxies.Add(_trustedProxy.Address);
            });
        }

        return app;
    }

    private static TestHttpContext CreateRequest(IPEndPoint peer, string path, string wireHost, string forwardedHost)
    {
        TestHttpContext context = TestHttpContext.Create(HttpMethod.Get, path, new HttpConnectionInfo(remoteEndPoint: peer));
        context.Request.Host = new HttpHost(wireHost);
        context.Request.Headers[HttpHeaderKey.XForwardedFor] = "203.0.113.9";
        context.Request.Headers[HttpHeaderKey.XForwardedHost] = forwardedHost;
        return context;
    }

    private static Route CreateRoute(string pattern, string host, HttpMethod? method = null)
    {
        return new Route(
            method ?? HttpMethod.Get,
            pattern,
            new RecordingRouterRouteHandler(),
            new RouterRouteMetadataCollection(new RouteHostMetadata(host)));
    }

    private sealed class ForwardedFeature : IHttpForwardedFeature
    {
        public ForwardedFeature(HttpHost host, HttpHost originalHost)
        {
            Host = host;
            OriginalHost = originalHost;
        }

        public string Name => "Assimalign.Cohesion.Web.Routing.Tests.Forwarded";

        public HttpScheme Scheme => HttpScheme.Http;

        public HttpHost Host { get; }

        public EndPoint? RemoteEndPoint => _trustedProxy;

        public IPAddress? RemoteIp => _trustedProxy.Address;

        public int RemotePort => _trustedProxy.Port;

        public HttpScheme OriginalScheme => HttpScheme.Http;

        public HttpHost OriginalHost { get; }

        public EndPoint? OriginalRemoteEndPoint => _trustedProxy;

        public int TrustedHopCount => 1;
    }
}
