using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Testing;

using NetHttpStatusCode = System.Net.HttpStatusCode;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Web.ForwardedHeaders.Tests;

/// <summary>
/// Full-pipeline coverage for the forwarded-headers middleware over the in-memory
/// transport (real HTTP/1.1 wire exchange through <see cref="WebApplicationTestFactory"/>).
/// The in-memory driver reports a non-IP remote endpoint, so the first hop is governed
/// by <see cref="ForwardedHeadersOptions.TrustLocalTransports"/> — exactly the trust
/// classification a Unix-domain-socket or named-pipe fronted deployment gets.
/// </summary>
public class ForwardedHeadersMiddlewareTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Registers the forwarded-headers middleware first and a terminal that echoes the
    /// resolved identity as <c>scheme|host|ip|port|hops|originalScheme</c>, reading it
    /// through the feature-first convention (<c>context.Effective*</c> + the feature).
    /// </summary>
    private static void ComposeEchoApplication(WebApplicationTestFactory factory, Action<ForwardedHeadersOptions> configure)
    {
        WebApplication app = factory.Application;

        app.UseForwardedHeaders(configure);
        app.Use(async (context, next) =>
        {
            IHttpForwardedFeature? feature = context.Features.Get<IHttpForwardedFeature>();

            string payload = string.Join('|',
                context.EffectiveScheme,
                context.EffectiveHost.Value,
                context.EffectiveRemoteIp?.ToString() ?? "none",
                feature?.RemotePort ?? -1,
                feature?.TrustedHopCount ?? -1,
                feature?.OriginalScheme.ToString() ?? "none");

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(payload), context.RequestCancelled);
        });
    }

    private static async Task<string> SendAsync(HttpClient client, CancellationToken cancellationToken, params (string Name, string Value)[] headers)
    {
        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/identity");
        foreach ((string name, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value).ShouldBeTrue();
        }

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    [Fact(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - Pipeline: A trusted chain should resolve the effective client end to end")]
    public async Task Pipeline_TrustedChain_ShouldResolveEffectiveClient()
    {
        // Arrange — first hop vouched by the local transport, second by KnownNetworks.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();

        ComposeEchoApplication(factory, options =>
        {
            options.Headers = ForwardedHeaderNames.XForwarded;
            options.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8"));
            options.ForwardLimit = null;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        string payload = await SendAsync(client, cancellation.Token,
            ("X-Forwarded-For", "203.0.113.9, 10.0.0.2"),
            ("X-Forwarded-Proto", "https"),
            ("X-Forwarded-Host", "public.example"));

        // Assert — deepest trusted values win; the wire scheme was plain http.
        payload.ShouldBe("Https|public.example|203.0.113.9|0|2|Http");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - Pipeline: An untrusted transport peer should leave the wire identity untouched (spoofing defense)")]
    public async Task Pipeline_UntrustedTransportPeer_ShouldIgnoreForwardingHeaders()
    {
        // Arrange — hardened trust model: the (non-IP) in-memory peer is not trusted, so
        // this models a client connecting directly and asserting a forwarded chain.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();

        ComposeEchoApplication(factory, options =>
        {
            options.Headers = ForwardedHeaderNames.All;
            options.TrustLocalTransports = false;
        });

        using HttpClient client = factory.CreateClient();

        // Act
        string payload = await SendAsync(client, cancellation.Token,
            ("Forwarded", "for=203.0.113.9;proto=https;host=spoofed.example"),
            ("X-Forwarded-For", "203.0.113.9"),
            ("X-Forwarded-Proto", "https"),
            ("X-Forwarded-Host", "spoofed.example"));

        // Assert — zero hops accepted; scheme/host/client stay at the wire values (the
        // in-memory transport reports no IP, hence "none").
        payload.ShouldBe("Http|localhost|none|0|0|Http");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - Pipeline: ForwardLimit should truncate the walk at the nearest hops")]
    public async Task Pipeline_ForwardLimit_ShouldTruncateWalk()
    {
        // Arrange — the default ForwardLimit of 1 accepts only the proxy-appended entry.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();

        ComposeEchoApplication(factory, options =>
        {
            options.Headers = ForwardedHeaderNames.XForwardedFor;
            options.KnownNetworks.Add(IPNetwork.Parse("10.0.0.0/8"));
        });

        using HttpClient client = factory.CreateClient();

        // Act
        string payload = await SendAsync(client, cancellation.Token,
            ("X-Forwarded-For", "203.0.113.9, 10.0.0.2"));

        // Assert — one hop: 10.0.0.2; the deeper (client-asserted) entry never applies.
        payload.ShouldBe("Http|localhost|10.0.0.2|0|1|Http");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - Pipeline: A trusted RFC 7239 element should resolve for, proto, and host together")]
    public async Task Pipeline_ForwardedElement_ShouldResolveAllValues()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();

        ComposeEchoApplication(factory, options => options.Headers = ForwardedHeaderNames.Forwarded);

        using HttpClient client = factory.CreateClient();

        // Act
        string payload = await SendAsync(client, cancellation.Token,
            ("Forwarded", "for=\"[2001:db8::1]:4711\";proto=https;host=api.example.com"));

        // Assert
        payload.ShouldBe("Https|api.example.com|2001:db8::1|4711|1|Http");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - Pipeline: A malformed Forwarded header should resolve nothing, even with a valid legacy header present")]
    public async Task Pipeline_MalformedForwardedHeader_ShouldResolveNothing()
    {
        // Arrange — both families honored; the RFC header is present but malformed, so
        // resolution is poisoned rather than falling back to X-Forwarded-For.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();

        ComposeEchoApplication(factory, options => options.Headers = ForwardedHeaderNames.All);

        using HttpClient client = factory.CreateClient();

        // Act
        string payload = await SendAsync(client, cancellation.Token,
            ("Forwarded", "for=203.0.113.9;=broken"),
            ("X-Forwarded-For", "203.0.113.77"));

        // Assert
        payload.ShouldBe("Http|localhost|none|0|0|Http");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - Pipeline: Raw request headers should pass through unmutated")]
    public async Task Pipeline_ResolvedExchange_ShouldNotMutateRawHeaders()
    {
        // Arrange — the middleware surfaces identity via the feature only; downstream
        // middleware must still see the original wire headers.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();

        WebApplication app = factory.Application;
        app.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        app.Use(async (context, next) =>
        {
            string payload = string.Join('|',
                context.Request.Headers[HttpHeaderKey.XForwardedFor].ToString(),
                context.Request.Headers[HttpHeaderKey.XForwardedProto].ToString(),
                context.Request.Scheme,
                context.EffectiveScheme);

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(payload), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act
        string payload = await SendAsync(client, cancellation.Token,
            ("X-Forwarded-For", "203.0.113.9"),
            ("X-Forwarded-Proto", "https"));

        // Assert — wire headers and wire scheme intact; only the effective view changed.
        payload.ShouldBe("203.0.113.9|https|Http|Https");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - Pipeline: Without the middleware, the Effective* members should fall back to wire values")]
    public async Task Pipeline_WithoutMiddleware_EffectiveMembersShouldFallBackToWireValues()
    {
        // Arrange — no forwarded-headers middleware registered at all.
        using CancellationTokenSource cancellation = new(TestTimeout);
        await using WebApplicationTestFactory factory = new();

        WebApplication app = factory.Application;
        app.Use(async (context, next) =>
        {
            string payload = string.Join('|',
                context.Features.Get<IHttpForwardedFeature>() is null ? "no-feature" : "feature",
                context.EffectiveScheme,
                context.EffectiveHost.Value,
                context.EffectiveRemoteIp?.ToString() ?? "none");

            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(payload), context.RequestCancelled);
        });

        using HttpClient client = factory.CreateClient();

        // Act — the client still asserts a chain; nothing consumes it.
        string payload = await SendAsync(client, cancellation.Token,
            ("X-Forwarded-For", "203.0.113.9"),
            ("X-Forwarded-Proto", "https"));

        // Assert
        payload.ShouldBe("no-feature|Http|localhost|none");
    }

    [Fact(DisplayName = "Cohesion Test [Web.ForwardedHeaders] - Pipeline: Composing with ForwardedHeaderNames.None should fail fast at build time")]
    public async Task Pipeline_HeadersNone_ShouldThrowAtCompositionTime()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        WebApplication app = factory.Application;

        // Act / Assert — misconfiguration surfaces when the pipeline is composed, not
        // silently at request time.
        Should.Throw<ArgumentException>(() => app.UseForwardedHeaders(options => { }));
    }
}
