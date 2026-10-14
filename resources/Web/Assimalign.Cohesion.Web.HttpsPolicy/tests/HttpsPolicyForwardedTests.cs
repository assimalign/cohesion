using System;
using System.Net;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.HttpsPolicy.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.HttpsPolicy.Tests;

/// <summary>
/// Proxy-awareness coverage (#1050): the HTTPS-policy middleware behind a simulated TLS-terminating
/// proxy. The real forwarded-headers middleware is captured through <see cref="TestPipelineBuilder"/>
/// and run in front of the policy middleware; the request arrives over plain HTTP from a proxy
/// address the forwarded-headers trust model knows, carrying the proxy's <c>X-Forwarded-*</c> headers.
/// </summary>
public class HttpsPolicyForwardedTests
{
    private static readonly IPEndPoint _trustedProxy = new(IPAddress.Parse("10.0.0.2"), 51000);
    private static readonly IPEndPoint _untrustedPeer = new(IPAddress.Parse("198.51.100.7"), 51000);

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - Forwarded: A trusted proxy's forwarded https should pass redirection without a loop")]
    public async Task Redirection_TrustedProxyForwardsHttps_ShouldPassThroughWithoutRedirect()
    {
        // Arrange — TLS terminated at the proxy; the app-facing hop is plaintext.
        IWebApplicationMiddleware forwarded = BuildForwardedHeaders();
        IWebApplicationMiddleware redirection = BuildRedirection();
        TestHttpContext context = CreateRequest(_trustedProxy, "backend.internal:8080",
            (HttpHeaderKey.XForwardedFor, "203.0.113.9"),
            (HttpHeaderKey.XForwardedProto, "https"),
            (HttpHeaderKey.XForwardedHost, "public.example"));
        bool nextCalled = false;

        // Act
        await forwarded.InvokeAsync(context, inner => redirection.InvokeAsync(inner, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }));

        // Assert — the wire scheme stayed http, but the effective scheme is https: no redirect.
        context.Request.Scheme.ShouldBe(HttpScheme.Http);
        nextCalled.ShouldBeTrue();
        context.Response.StatusCode.Value.ShouldBe(200);
        context.Response.Headers.ContainsKey(HttpHeaderKey.Location).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - Forwarded: A trusted proxy's forwarded http should redirect to the forwarded host")]
    public async Task Redirection_TrustedProxyForwardsHttp_ShouldRedirectToForwardedHost()
    {
        // Arrange — the client used plaintext; the Location must target the host the client addressed,
        // not the proxy's upstream authority, with the forwarded port swapped for the HTTPS port.
        IWebApplicationMiddleware forwarded = BuildForwardedHeaders();
        IWebApplicationMiddleware redirection = BuildRedirection();
        TestHttpContext context = CreateRequest(_trustedProxy, "backend.internal:8080",
            (HttpHeaderKey.XForwardedFor, "203.0.113.9"),
            (HttpHeaderKey.XForwardedProto, "http"),
            (HttpHeaderKey.XForwardedHost, "public.example:8080"));
        bool nextCalled = false;

        // Act
        await forwarded.InvokeAsync(context, inner => redirection.InvokeAsync(inner, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }));

        // Assert
        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.Value.ShouldBe(307);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://public.example/page");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - Forwarded: A trusted proxy's forwarded https should receive HSTS")]
    public async Task Hsts_TrustedProxyForwardsHttps_ShouldEmitPolicy()
    {
        // Arrange
        IWebApplicationMiddleware forwarded = BuildForwardedHeaders();
        IWebApplicationMiddleware hsts = BuildHsts();
        TestHttpContext context = CreateRequest(_trustedProxy, "backend.internal:8080",
            (HttpHeaderKey.XForwardedFor, "203.0.113.9"),
            (HttpHeaderKey.XForwardedProto, "https"),
            (HttpHeaderKey.XForwardedHost, "public.example"));

        // Act
        await forwarded.InvokeAsync(context, inner => hsts.InvokeAsync(inner, Terminal));

        // Assert — RFC 6797 §7.2 is satisfied by the proxy's TLS leg the user agent receives it over.
        context.Response.Headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=31536000");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - Forwarded: A local proxy dialing localhost should not suppress HSTS for the public host")]
    public async Task Hsts_LocalProxyDialsLoopback_ShouldMatchExclusionsOnForwardedHost()
    {
        // Arrange — a same-host reverse proxy connects over loopback (a default KnownProxies entry) and
        // addresses the app as localhost; the excluded-host check must use the public host.
        IWebApplicationMiddleware forwarded = BuildForwardedHeaders();
        IWebApplicationMiddleware hsts = BuildHsts();
        TestHttpContext context = CreateRequest(new IPEndPoint(IPAddress.Loopback, 51000), "localhost:5000",
            (HttpHeaderKey.XForwardedFor, "203.0.113.9"),
            (HttpHeaderKey.XForwardedProto, "https"),
            (HttpHeaderKey.XForwardedHost, "public.example"));

        // Act
        await forwarded.InvokeAsync(context, inner => hsts.InvokeAsync(inner, Terminal));

        // Assert
        context.Response.Headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=31536000");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - Forwarded: HSTS registered ahead of UseForwardedHeaders should still honor the forwarded scheme")]
    public async Task Hsts_RegisteredBeforeForwardedHeaders_ShouldEmitPolicy()
    {
        // Arrange — HSTS reads the effective scheme after next returns, by which point the downstream
        // forwarded-headers middleware has attached its feature.
        IWebApplicationMiddleware forwarded = BuildForwardedHeaders();
        IWebApplicationMiddleware hsts = BuildHsts();
        TestHttpContext context = CreateRequest(_trustedProxy, "backend.internal:8080",
            (HttpHeaderKey.XForwardedFor, "203.0.113.9"),
            (HttpHeaderKey.XForwardedProto, "https"),
            (HttpHeaderKey.XForwardedHost, "public.example"));

        // Act
        await hsts.InvokeAsync(context, inner => forwarded.InvokeAsync(inner, Terminal));

        // Assert
        context.Response.Headers[HttpHeaderKey.StrictTransportSecurity].Value.ShouldBe("max-age=31536000");
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - Forwarded: Without UseForwardedHeaders a forwarded https header should be ignored")]
    public async Task Policy_ForwardedProtoWithoutForwardedHeaders_ShouldUseWireScheme()
    {
        // Arrange — no forwarded-headers middleware: the effective values fall back to the transport's,
        // so a client asserting X-Forwarded-Proto is still redirected and never receives HSTS.
        IWebApplicationMiddleware redirection = BuildRedirection();
        IWebApplicationMiddleware hsts = BuildHsts();
        TestHttpContext redirected = CreateRequest(_trustedProxy, "example.com",
            (HttpHeaderKey.XForwardedProto, "https"),
            (HttpHeaderKey.XForwardedHost, "spoofed.example"));
        TestHttpContext unprotected = CreateRequest(_trustedProxy, "example.com",
            (HttpHeaderKey.XForwardedProto, "https"));

        // Act
        await redirection.InvokeAsync(redirected, Terminal);
        await hsts.InvokeAsync(unprotected, Terminal);

        // Assert
        redirected.Response.StatusCode.Value.ShouldBe(307);
        redirected.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.com/page");
        unprotected.Response.Headers.ContainsKey(HttpHeaderKey.StrictTransportSecurity).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.HttpsPolicy] - Forwarded: An untrusted peer's forwarded https should still be redirected")]
    public async Task Redirection_UntrustedPeerForwardsHttps_ShouldRedirectOnWireValues()
    {
        // Arrange — the peer is outside KnownProxies, so the trust walk accepts no hop.
        IWebApplicationMiddleware forwarded = BuildForwardedHeaders();
        IWebApplicationMiddleware redirection = BuildRedirection();
        TestHttpContext context = CreateRequest(_untrustedPeer, "example.com",
            (HttpHeaderKey.XForwardedFor, "203.0.113.9"),
            (HttpHeaderKey.XForwardedProto, "https"),
            (HttpHeaderKey.XForwardedHost, "spoofed.example"));

        // Act
        await forwarded.InvokeAsync(context, inner => redirection.InvokeAsync(inner, Terminal));

        // Assert
        context.Response.StatusCode.Value.ShouldBe(307);
        context.Response.Headers[HttpHeaderKey.Location].Value.ShouldBe("https://example.com/page");
    }

    private static TestHttpContext CreateRequest(EndPoint peer, string wireHost, params (HttpHeaderKey Key, string Value)[] headers)
    {
        TestHttpContext context = new(HttpScheme.Http, wireHost, "/page")
        {
            ConnectionInfo = new HttpConnectionInfo(remoteEndPoint: peer),
        };

        foreach ((HttpHeaderKey key, string value) in headers)
        {
            context.Request.Headers[key] = value;
        }

        return context;
    }

    private static IWebApplicationMiddleware BuildForwardedHeaders()
    {
        TestPipelineBuilder builder = new();
        builder.UseForwardedHeaders(options =>
        {
            options.Headers = ForwardedHeaderNames.XForwarded;
            options.KnownProxies.Add(_trustedProxy.Address);
        });

        return builder.LastMiddleware.ShouldNotBeNull();
    }

    private static IWebApplicationMiddleware BuildRedirection(Action<HttpsRedirectionOptions>? configure = null)
    {
        TestPipelineBuilder builder = new();
        builder.UseHttpsRedirection(configure);

        return builder.LastMiddleware.ShouldNotBeNull();
    }

    private static IWebApplicationMiddleware BuildHsts(Action<HstsOptions>? configure = null)
    {
        TestPipelineBuilder builder = new();
        builder.UseHsts(configure);

        return builder.LastMiddleware.ShouldNotBeNull();
    }

    private static Task Terminal(IHttpContext context) => Task.CompletedTask;
}
