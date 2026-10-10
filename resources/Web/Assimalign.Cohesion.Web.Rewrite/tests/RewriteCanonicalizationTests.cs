using System;
using System.Net;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.Rewrite.Tests.TestObjects;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Rewrite.Tests;

/// <summary>
/// The canonicalization helpers: HTTPS, <c>www</c> and non-<c>www</c>, trailing slash added and removed, and
/// lowercase path, including the effective scheme and host a trusted proxy forwards (the real
/// forwarded-headers middleware runs ahead of the rules), the hosts never redirected, and idempotency.
/// </summary>
public class RewriteCanonicalizationTests
{
    private static readonly IPEndPoint _trustedProxy = new(IPAddress.Parse("10.0.0.2"), 51000);
    private static readonly IPEndPoint _untrustedPeer = new(IPAddress.Parse("198.51.100.7"), 51000);

    // ------------------------------------------------------------------ HTTPS

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - HTTPS: A plaintext request should be redirected to https with 308 by default")]
    public async Task AddRedirectToHttps_PlaintextRequest_ShouldRedirect()
    {
        // Arrange
        (TestPipelineBuilder builder, DownstreamProbe probe) = Create(rules => rules.AddRedirectToHttps());

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/page", "a=1"));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(308);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.com/page?a=1");
        probe.Invocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - HTTPS: A secure request should pass through")]
    public async Task AddRedirectToHttps_SecureRequest_ShouldPassThrough()
    {
        // Arrange
        (TestPipelineBuilder builder, DownstreamProbe probe) = Create(rules => rules.AddRedirectToHttps());

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/page", scheme: HttpScheme.Https));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(200);
        probe.Invocations.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - HTTPS: The inbound port should be replaced by the HTTPS port, omitted when 443")]
    [InlineData("example.com:8080", 443, "https://example.com/page")]
    [InlineData("example.com", 8443, "https://example.com:8443/page")]
    [InlineData("[::1]:8080", 443, "https://[::1]/page")]
    [InlineData("[2001:db8::1]", 8443, "https://[2001:db8::1]:8443/page")]
    public async Task AddRedirectToHttps_Ports_ShouldTargetHttpsPort(string host, int httpsPort, string location)
    {
        // Arrange
        (TestPipelineBuilder builder, _) = Create(rules => rules.AddRedirectToHttps(HttpStatusCode.MovedPermanently, httpsPort));

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/page", host: host));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(301);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe(location);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - HTTPS: A host that cannot be echoed safely should not be redirected")]
    [InlineData("")]
    [InlineData("evil.example/path")]
    [InlineData("user@evil.example")]
    [InlineData("evil.example\\x")]
    public async Task AddRedirectToHttps_UnsafeHost_ShouldPassThrough(string host)
    {
        // Arrange
        (TestPipelineBuilder builder, DownstreamProbe probe) = Create(rules => rules.AddRedirectToHttps());

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/page", host: host));

        // Assert
        context.Response.Headers.ContainsKey(HttpHeaderKey.Location).ShouldBeFalse();
        probe.Invocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - HTTPS: A trusted proxy's forwarded https should pass through without a loop")]
    public async Task AddRedirectToHttps_TrustedProxyForwardsHttps_ShouldPassThrough()
    {
        // Arrange — TLS ends at the proxy; the hop to the application is plaintext.
        (TestPipelineBuilder builder, DownstreamProbe probe) = CreateBehindProxy(rules => rules.AddRedirectToHttps());
        TestHttpContext context = Proxied(_trustedProxy, "backend.internal:8080", "https", "public.example");

        // Act
        await builder.SendAsync(context);

        // Assert
        context.Request.Scheme.ShouldBe(HttpScheme.Http);
        context.Response.Headers.ContainsKey(HttpHeaderKey.Location).ShouldBeFalse();
        probe.Invocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - HTTPS: A trusted proxy's forwarded http should redirect to the host the client addressed")]
    public async Task AddRedirectToHttps_TrustedProxyForwardsHttp_ShouldRedirectToForwardedHost()
    {
        // Arrange
        (TestPipelineBuilder builder, _) = CreateBehindProxy(rules => rules.AddRedirectToHttps());
        TestHttpContext context = Proxied(_trustedProxy, "backend.internal:8080", "http", "public.example:8080");

        // Act
        await builder.SendAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(308);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://public.example/page");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - HTTPS: An untrusted peer's forwarded https should be redirected on the wire values")]
    public async Task AddRedirectToHttps_UntrustedPeerForwardsHttps_ShouldRedirectOnWireValues()
    {
        // Arrange
        (TestPipelineBuilder builder, _) = CreateBehindProxy(rules => rules.AddRedirectToHttps());
        TestHttpContext context = Proxied(_untrustedPeer, "example.com", "https", "spoofed.example");

        // Act
        await builder.SendAsync(context);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(308);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.com/page");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - HTTPS: An HTTPS port outside 1-65535 should be rejected at registration")]
    public void AddRedirectToHttps_InvalidPort_ShouldThrow()
    {
        // Arrange
        RewriteOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddRedirectToHttps(HttpStatusCode.PermanentRedirect, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => options.AddRedirectToHttps(HttpStatusCode.PermanentRedirect, 65536));
    }

    // ------------------------------------------------------------------ www / non-www

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Host: A bare host should be redirected to www with 308 by default")]
    public async Task AddRedirectToWww_BareHost_ShouldRedirectToWww()
    {
        // Arrange
        (TestPipelineBuilder builder, DownstreamProbe probe) = Create(rules => rules.AddRedirectToWww());

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/page", "a=1"));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(308);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("http://www.example.com/page?a=1");
        probe.Invocations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Host: A www host should pass the www rule, so the redirect cannot loop")]
    public async Task AddRedirectToWww_WwwHost_ShouldPassThrough()
    {
        // Arrange
        (TestPipelineBuilder builder, DownstreamProbe probe) = Create(rules => rules.AddRedirectToWww());

        // Act
        await builder.SendAsync(new TestHttpContext("/page", host: "WWW.example.com"));

        // Assert
        probe.Invocations.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Host: localhost and IP literals should never be redirected")]
    [InlineData("localhost:5000")]
    [InlineData("app.localhost")]
    [InlineData("127.0.0.1:8080")]
    [InlineData("[::1]:8080")]
    [InlineData("www.localhost")]
    public async Task AddRedirectToWwwOrNonWww_LocalOrAddressHost_ShouldPassThrough(string host)
    {
        // Arrange
        (TestPipelineBuilder toWww, DownstreamProbe toWwwProbe) = Create(rules => rules.AddRedirectToWww());
        (TestPipelineBuilder toNonWww, DownstreamProbe toNonWwwProbe) = Create(rules => rules.AddRedirectToNonWww());

        // Act
        await toWww.SendAsync(new TestHttpContext("/page", host: host));
        await toNonWww.SendAsync(new TestHttpContext("/page", host: host));

        // Assert
        toWwwProbe.Invocations.ShouldBe(1);
        toNonWwwProbe.Invocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Host: The www rule should keep the effective scheme and the port")]
    public async Task AddRedirectToWww_SecureRequestWithPort_ShouldKeepSchemeAndPort()
    {
        // Arrange
        (TestPipelineBuilder builder, _) = Create(rules => rules.AddRedirectToWww(HttpStatusCode.MovedPermanently));

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext("/page", scheme: HttpScheme.Https, host: "example.com:8443"));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(301);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://www.example.com:8443/page");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Host: Configured domains should limit the www rule")]
    public async Task AddRedirectToWww_Domains_ShouldLimitTheRule()
    {
        // Arrange
        (TestPipelineBuilder builder, DownstreamProbe probe) = Create(rules => rules.AddRedirectToWww("example.com", "example.org"));

        // Act
        TestHttpContext listed = await builder.SendAsync(new TestHttpContext("/page", host: "EXAMPLE.org"));
        await builder.SendAsync(new TestHttpContext("/page", host: "api.example.com"));

        // Assert
        listed.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("http://www.EXAMPLE.org/page");
        probe.Invocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Host: The www rule should redirect to the host a trusted proxy forwarded")]
    public async Task AddRedirectToWww_TrustedProxy_ShouldUseForwardedSchemeAndHost()
    {
        // Arrange
        (TestPipelineBuilder builder, _) = CreateBehindProxy(rules => rules.AddRedirectToWww());
        TestHttpContext context = Proxied(_trustedProxy, "backend.internal:8080", "https", "public.example");

        // Act
        await builder.SendAsync(context);

        // Assert
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://www.public.example/page");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Host: A www host should be redirected to the bare host")]
    public async Task AddRedirectToNonWww_WwwHost_ShouldRedirectToBareHost()
    {
        // Arrange
        (TestPipelineBuilder builder, DownstreamProbe probe) = Create(rules => rules.AddRedirectToNonWww());

        // Act
        TestHttpContext redirected = await builder.SendAsync(new TestHttpContext("/page", "a=1", host: "www.example.com"));
        await builder.SendAsync(new TestHttpContext("/page", host: "example.com"));

        // Assert
        redirected.Response.StatusCode.Value.ShouldBe(308);
        redirected.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("http://example.com/page?a=1");
        probe.Invocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Host: Configured domains should limit the non-www rule")]
    public async Task AddRedirectToNonWww_Domains_ShouldLimitTheRule()
    {
        // Arrange
        (TestPipelineBuilder builder, DownstreamProbe probe) = Create(rules => rules.AddRedirectToNonWww(HttpStatusCode.Found, "www.example.com"));

        // Act
        TestHttpContext listed = await builder.SendAsync(new TestHttpContext("/page", host: "www.example.com"));
        await builder.SendAsync(new TestHttpContext("/page", host: "www.example.org"));

        // Assert
        listed.Response.StatusCode.Value.ShouldBe(302);
        listed.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("http://example.com/page");
        probe.Invocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Host: An empty domain should be rejected at registration")]
    public void AddRedirectToWww_EmptyDomain_ShouldThrow()
    {
        // Arrange
        RewriteOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() => options.AddRedirectToWww("example.com", " "));
        Should.Throw<ArgumentException>(() => options.AddRedirectToNonWww(HttpStatusCode.PermanentRedirect, (string)null!));
    }

    // ------------------------------------------------------------------ Trailing slash

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Slash: A directory path should be redirected to its trailing-slash form")]
    [InlineData("/docs", "a=1", "/docs/?a=1")]
    [InlineData("/v1.2/docs", null, "/v1.2/docs/")]
    public async Task AddRedirectToTrailingSlash_DirectoryPath_ShouldRedirect(string path, string? query, string location)
    {
        // Arrange
        (TestPipelineBuilder builder, _) = Create(rules => rules.AddRedirectToTrailingSlash());

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext(path, query));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(308);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe(location);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Slash: The root, a slashed path and a file path should not get a trailing slash")]
    [InlineData("/")]
    [InlineData("/docs/")]
    [InlineData("/app.js")]
    [InlineData("/assets/site.min.css")]
    public async Task AddRedirectToTrailingSlash_CanonicalOrFilePath_ShouldPassThrough(string path)
    {
        // Arrange
        (TestPipelineBuilder builder, DownstreamProbe probe) = Create(rules => rules.AddRedirectToTrailingSlash());

        // Act
        await builder.SendAsync(new TestHttpContext(path));

        // Assert
        probe.Invocations.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Slash: A trailing slash should be removed in one hop")]
    [InlineData("/docs/", "/docs")]
    [InlineData("/docs///", "/docs")]
    [InlineData("/a/b/", "/a/b")]
    public async Task AddRedirectToNoTrailingSlash_SlashedPath_ShouldRedirect(string path, string location)
    {
        // Arrange
        (TestPipelineBuilder builder, _) = Create(rules => rules.AddRedirectToNoTrailingSlash(HttpStatusCode.MovedPermanently));

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext(path, "q=1"));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(301);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe(location + "?q=1");
    }

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Slash: The root and an unslashed path should keep their form")]
    [InlineData("/")]
    [InlineData("/docs")]
    public async Task AddRedirectToNoTrailingSlash_CanonicalPath_ShouldPassThrough(string path)
    {
        // Arrange
        (TestPipelineBuilder builder, DownstreamProbe probe) = Create(rules => rules.AddRedirectToNoTrailingSlash());

        // Act
        await builder.SendAsync(new TestHttpContext(path));

        // Assert
        probe.Invocations.ShouldBe(1);
    }

    // ------------------------------------------------------------------ Lowercase

    [Theory(DisplayName = "Cohesion Test [Web.Rewrite] - Case: A path with uppercase letters should be redirected to lowercase, the query untouched")]
    [InlineData("/Docs/Intro", "Q=A", "/docs/intro?Q=A")]
    [InlineData("/CAFÉ", null, "/caf%C3%A9")]
    public async Task AddRedirectToLowercase_UppercasePath_ShouldRedirect(string path, string? query, string location)
    {
        // Arrange
        (TestPipelineBuilder builder, _) = Create(rules => rules.AddRedirectToLowercase());

        // Act
        TestHttpContext context = await builder.SendAsync(new TestHttpContext(path, query));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(308);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe(location);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Case: A lowercase path should pass, so the redirect cannot loop")]
    public async Task AddRedirectToLowercase_LowercasePath_ShouldPassThrough()
    {
        // Arrange
        (TestPipelineBuilder builder, DownstreamProbe probe) = Create(rules => rules.AddRedirectToLowercase());

        // Act
        await builder.SendAsync(new TestHttpContext("/docs/intro", "Q=A"));

        // Assert
        probe.Invocations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Rewrite] - Canonical: Helpers should apply in order, each answering on its own")]
    public async Task CanonicalHelpers_InOrder_ShouldAnswerFirstMismatch()
    {
        // Arrange — the scheme is canonicalized first; once it is https the host rule answers.
        (TestPipelineBuilder builder, DownstreamProbe probe) = Create(rules => rules
            .AddRedirectToHttps()
            .AddRedirectToNonWww()
            .AddRedirectToLowercase());

        // Act
        TestHttpContext plaintext = await builder.SendAsync(new TestHttpContext("/Docs", host: "www.example.com"));
        TestHttpContext secureWww = await builder.SendAsync(new TestHttpContext("/Docs", scheme: HttpScheme.Https, host: "www.example.com"));
        TestHttpContext secureBare = await builder.SendAsync(new TestHttpContext("/Docs", scheme: HttpScheme.Https, host: "example.com"));
        await builder.SendAsync(new TestHttpContext("/docs", scheme: HttpScheme.Https, host: "example.com"));

        // Assert
        plaintext.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://www.example.com/Docs");
        secureWww.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.com/Docs");
        secureBare.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("/docs");
        probe.Invocations.ShouldBe(1);
    }

    private static (TestPipelineBuilder Builder, DownstreamProbe Probe) Create(Action<RewriteOptions> configure)
    {
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseRewrite(configure);
        builder.Run(probe.InvokeAsync);
        return (builder, probe);
    }

    private static (TestPipelineBuilder Builder, DownstreamProbe Probe) CreateBehindProxy(Action<RewriteOptions> configure)
    {
        DownstreamProbe probe = new();
        TestPipelineBuilder builder = new();
        builder.UseForwardedHeaders(options =>
        {
            options.Headers = ForwardedHeaderNames.XForwarded;
            options.KnownProxies.Add(_trustedProxy.Address);
        });
        builder.UseRewrite(configure);
        builder.Run(probe.InvokeAsync);
        return (builder, probe);
    }

    private static TestHttpContext Proxied(IPEndPoint peer, string wireHost, string forwardedProto, string forwardedHost)
    {
        TestHttpContext context = new("/page", host: wireHost)
        {
            ConnectionInfo = new HttpConnectionInfo(remoteEndPoint: peer),
        };

        context.Request.Headers[HttpHeaderKey.XForwardedFor] = "203.0.113.9";
        context.Request.Headers[HttpHeaderKey.XForwardedProto] = forwardedProto;
        context.Request.Headers[HttpHeaderKey.XForwardedHost] = forwardedHost;
        return context;
    }
}
