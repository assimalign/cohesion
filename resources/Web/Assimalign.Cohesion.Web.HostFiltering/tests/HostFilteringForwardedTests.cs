using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpMethod = System.Net.Http.HttpMethod;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.HostFiltering.Tests;

/// <summary>
/// Proxy-awareness coverage (#1050): the guard validates the effective host. The real
/// forwarded-headers middleware runs in front of it over <see cref="WebApplicationTestFactory"/>,
/// whose in-memory peer is trusted as a local transport — the simulated proxy that forwards
/// <c>X-Forwarded-Host</c> while the wire <c>Host</c> names its upstream authority.
/// </summary>
public class HostFilteringForwardedTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - Forwarded: An allowlisted forwarded host should be accepted even when the wire host is not")]
    public async Task HostFiltering_TrustedProxyForwardsAllowedHost_ShouldAccept()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();

        factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("public.example"));
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act — the proxy dialed its upstream name; the client addressed public.example.
        using HttpResponseMessage response = await SendAsync(client, "http://internal.upstream/", "public.example", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - Forwarded: A forwarded host outside the allowlist should be rejected even when the wire host is allowed")]
    public async Task HostFiltering_TrustedProxyForwardsUnlistedHost_ShouldReject()
    {
        // Arrange — the wire host is allowlisted, but the host every downstream consumer reads is not.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();

        factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("public.example"));
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, "http://public.example/", "evil.example", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - Forwarded: Without UseForwardedHeaders the wire host should be validated and X-Forwarded-Host ignored")]
    public async Task HostFiltering_WithoutForwardedHeaders_ShouldValidateWireHost()
    {
        // Arrange — the effective host falls back to the wire host; the header is not believed.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();

        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("public.example"));
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage spoofedAllowed = await SendAsync(client, "http://internal.upstream/", "public.example", cancellation.Token);
        using HttpResponseMessage wireAllowed = await SendAsync(client, "http://public.example/", "evil.example", cancellation.Token);

        // Assert
        spoofedAllowed.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
        wireAllowed.StatusCode.ShouldBe(NetHttpStatusCode.OK);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - Forwarded: Registered ahead of UseForwardedHeaders the guard should validate the wire host")]
    public async Task HostFiltering_RegisteredBeforeForwardedHeaders_ShouldValidateWireHost()
    {
        // Arrange — the documented alternative ordering: the feature does not exist yet when the guard
        // runs, so it bounds the upstream authority instead of the forwarded host.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();

        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("public.example"));
        factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, "http://internal.upstream/", "public.example", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.HostFiltering] - Forwarded: A forwarded host from an untrusted peer should not be validated in place of the wire host")]
    public async Task HostFiltering_UntrustedPeerForwardsAllowedHost_ShouldValidateWireHost()
    {
        // Arrange — the local transport is not trusted, so the trust walk accepts no hop.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = new();

        factory.Application.UseForwardedHeaders(options =>
        {
            options.Headers = ForwardedHeaderNames.XForwarded;
            options.TrustLocalTransports = false;
        });
        factory.Application.UseHostFiltering(options => options.AllowedHosts.Add("public.example"));
        UseTerminalOkHandler(factory.Application);

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await SendAsync(client, "http://internal.upstream/", "public.example", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.BadRequest);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string uri, string forwardedHost, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(NetHttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", forwardedHost).ShouldBeTrue();

        return await client.SendAsync(request, cancellationToken);
    }

    private static void UseTerminalOkHandler(WebApplication application)
    {
        application.Use((context, next) =>
        {
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
            return Task.CompletedTask;
        });
    }
}
