using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.CookiePolicy.Tests.TestObjects;
using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.CookiePolicy.Tests;

/// <summary>
/// The <see cref="CookieSecurePolicy.SameAsRequest"/> decision reads the effective request scheme. The
/// in-memory transport's peer is a local transport, which the forwarded-headers middleware trusts by
/// default, so a request carrying <c>X-Forwarded-Proto: https</c> models a client behind a trusted
/// TLS-terminating proxy.
/// </summary>
public class CookiePolicyForwardedTests
{
    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Forwarded: A trusted TLS-terminating proxy's https should make SameAsRequest add Secure")]
    public async Task UseCookiePolicy_TrustedProxyForwardsHttps_ShouldAddSecure()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        factory.Application.UseCookiePolicy();
        UseWriter(factory);

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory, CookiePolicyTestHost.ForwardedHttps);

        // Assert
        fields.ShouldHaveSingleItem().Raw.ShouldBe("a=1; Path=/; Secure");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Forwarded: A proxy forwarding http should leave the cookie without Secure")]
    public async Task UseCookiePolicy_TrustedProxyForwardsHttp_ShouldNotAddSecure()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        factory.Application.UseCookiePolicy();
        UseWriter(factory);

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory, ("X-Forwarded-For", "203.0.113.9"), ("X-Forwarded-Proto", "http"));

        // Assert
        fields.ShouldHaveSingleItem().Has("Secure").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Forwarded: Without UseForwardedHeaders a spoofed X-Forwarded-Proto should not add Secure")]
    public async Task UseCookiePolicy_WithoutForwardedHeaders_ShouldUseTheTransportScheme()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy();
        UseWriter(factory);

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory, CookiePolicyTestHost.ForwardedHttps);

        // Assert
        fields.ShouldHaveSingleItem().Has("Secure").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Forwarded: The effective scheme should be read when the cookie is appended, wherever UseForwardedHeaders sits")]
    public async Task UseCookiePolicy_RegisteredAheadOfForwardedHeaders_ShouldStillReadTheEffectiveScheme()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy();
        factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        UseWriter(factory);

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory, CookiePolicyTestHost.ForwardedHttps);

        // Assert
        fields.ShouldHaveSingleItem().Has("Secure").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Forwarded: Behind a trusted TLS proxy the Secure floor should satisfy the __Host- and SameSite=None rules under Reject")]
    public async Task UseCookiePolicy_PrefixedAndCrossSiteCookiesBehindTlsProxyUnderReject_ShouldBeIssued()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        factory.Application.UseCookiePolicy(options =>
        {
            options.PrefixViolation = CookieViolationAction.Reject;
            options.SameSiteNoneWithoutSecure = CookieViolationAction.Reject;
            options.OnRejected = recorder.Record;
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(new HttpCookie("__Host-id", "1"));
            context.Response.Cookies.Add(new HttpCookie("embed", "1", new HttpCookieOptions { SameSite = HttpCookieSameSiteMode.None }));
        });

        // Act
        IReadOnlyList<SetCookieField> overHttps = await SendAsync(factory, CookiePolicyTestHost.ForwardedHttps);
        IReadOnlyList<SetCookieField> overHttp = await SendAsync(factory);

        // Assert — over https the floor makes both compliant; over http the same cookies are rejected.
        overHttps.Count.ShouldBe(2);
        overHttps.ShouldAllBe(field => field.Has("Secure"));
        overHttp.ShouldBeEmpty();
        recorder.Rejections.ShouldBe(
        [
            ("__Host-id", CookiePolicyRejectionReason.HostPrefixViolation),
            ("embed", CookiePolicyRejectionReason.SameSiteNoneWithoutSecure),
        ]);
    }

    private static void UseWriter(WebApplicationTestFactory factory)
        => CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("a", "1")));

    private static async Task<IReadOnlyList<SetCookieField>> SendAsync(
        WebApplicationTestFactory factory,
        params (string Name, string Value)[] headers)
    {
        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();
        return await CookiePolicyTestHost.SendAsync(client, cancellation.Token, headers);
    }
}
