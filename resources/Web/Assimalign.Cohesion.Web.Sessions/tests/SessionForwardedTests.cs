using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.Sessions.Internal;
using Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.Sessions.Tests;

/// <summary>
/// Proxy-awareness coverage (#1050): the session-id cookie's <c>Secure</c> decision reads the
/// effective scheme. End-to-end cases put the real forwarded-headers middleware in front of
/// <c>UseSessions</c> over <see cref="WebApplicationTestFactory"/>, whose in-memory peer is trusted as a
/// local transport; unit cases run the same middleware over <see cref="SessionTestContext"/> with a
/// plaintext request from an explicit proxy address.
/// </summary>
public class SessionForwardedTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
    private static readonly IPEndPoint _trustedProxy = new(IPAddress.Parse("10.0.0.2"), 51000);
    private static readonly IPEndPoint _untrustedPeer = new(IPAddress.Parse("198.51.100.7"), 51000);

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Forwarded: A trusted TLS-terminating proxy should get a Secure session cookie")]
    public async Task UseSessions_TrustedProxyForwardsHttps_ShouldMarkCookieSecure()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();

        factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        factory.Application.UseSessions();
        UseSessionTouchingHandler(factory);

        using HttpClient client = factory.CreateClient();

        // Act — plaintext on the wire; the proxy asserts the client used https.
        string setCookie = await SendForSetCookieAsync(client, cancellation.Token,
            ("X-Forwarded-For", "203.0.113.9"),
            ("X-Forwarded-Proto", "https"));

        // Assert
        HasAttribute(setCookie, "Secure").ShouldBeTrue();
        HasAttribute(setCookie, "HttpOnly").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Forwarded: Without UseForwardedHeaders a spoofed X-Forwarded-Proto should not mark the cookie Secure")]
    public async Task UseSessions_WithoutForwardedHeaders_ShouldUseTransportScheme()
    {
        // Arrange — no forwarded-headers middleware: the effective scheme is the wire scheme (http).
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();

        factory.Application.UseSessions();
        UseSessionTouchingHandler(factory);

        using HttpClient client = factory.CreateClient();

        // Act
        string setCookie = await SendForSetCookieAsync(client, cancellation.Token,
            ("X-Forwarded-Proto", "https"));

        // Assert
        HasAttribute(setCookie, "Secure").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Forwarded: A plaintext request from a trusted proxy address forwarding https should get a Secure cookie")]
    public async Task Invoke_TrustedProxyAddressForwardsHttps_ShouldMarkCookieSecure()
    {
        // Arrange
        SessionTestContext context = CreateRequest(_trustedProxy);

        // Act
        await RunBehindForwardedHeadersAsync(context);

        // Assert — the wire scheme is still http; only the effective view changed.
        context.Request.Scheme.ShouldBe(HttpScheme.Http);
        context.Response.Cookies.ShouldHaveSingleItem().Options.Secure.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Sessions] - Forwarded: An untrusted peer forwarding https should not get a Secure cookie")]
    public async Task Invoke_UntrustedPeerForwardsHttps_ShouldNotMarkCookieSecure()
    {
        // Arrange — the peer is outside KnownProxies, so the trust walk accepts no hop.
        SessionTestContext context = CreateRequest(_untrustedPeer);

        // Act
        await RunBehindForwardedHeadersAsync(context);

        // Assert
        context.Response.Cookies.ShouldHaveSingleItem().Options.Secure.ShouldBeFalse();
    }

    private static SessionTestContext CreateRequest(EndPoint peer)
    {
        SessionTestContext context = new(HttpScheme.Http)
        {
            ConnectionInfo = new HttpConnectionInfo(remoteEndPoint: peer),
        };
        context.Request.Headers[HttpHeaderKey.XForwardedFor] = "203.0.113.9";
        context.Request.Headers[HttpHeaderKey.XForwardedProto] = "https";

        return context;
    }

    private static async Task RunBehindForwardedHeadersAsync(SessionTestContext context)
    {
        TestPipelineBuilder builder = new();
        builder.UseForwardedHeaders(options =>
        {
            options.Headers = ForwardedHeaderNames.XForwarded;
            options.KnownProxies.Add(_trustedProxy.Address);
        });
        IWebApplicationMiddleware forwarded = builder.LastMiddleware.ShouldNotBeNull();

        SessionMiddleware sessions = new(new InMemoryHttpSessionStore(), new HttpSessionOptions());

        await forwarded.InvokeAsync(context, inner => sessions.InvokeAsync(inner, async exchange =>
        {
            IHttpSession session = await exchange.LoadSessionAsync();
            session.SetString("user", "alice");
        }));
    }

    private static void UseSessionTouchingHandler(WebApplicationTestFactory factory)
    {
        factory.Application.Use(async (context, next) =>
        {
            IHttpSession session = await context.LoadSessionAsync(context.RequestCancelled);
            session.SetString("user", "alice");
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        });
    }

    private static async Task<string> SendForSetCookieAsync(HttpClient client, CancellationToken cancellationToken, params (string Name, string Value)[] headers)
    {
        using HttpRequestMessage request = new(NetHttpMethod.Get, "/");
        foreach ((string name, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value).ShouldBeTrue();
        }

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        // GetValues throws when the header is absent, which fails the test as intended.
        return response.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem();
    }

    private static bool HasAttribute(string setCookie, string attribute)
        => setCookie
            .Split(';')
            .Skip(1)
            .Any(part => string.Equals(part.Trim(), attribute, StringComparison.OrdinalIgnoreCase));
}
