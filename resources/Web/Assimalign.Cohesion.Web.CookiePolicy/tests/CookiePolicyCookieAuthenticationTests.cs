using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authentication.Cookie;
using Assimalign.Cohesion.Web.CookiePolicy.Tests.TestObjects;
using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.CookiePolicy.Tests;

/// <summary>
/// Cookie authentication under the cookie policy: the sign-in cookie is essential by default, so a consent
/// requirement never locks a user out; it is <c>Secure</c> behind a trusted TLS-terminating proxy; the
/// policy's floors apply to it like any other cookie; and its deletion always passes.
/// </summary>
public sealed class CookiePolicyCookieAuthenticationTests : IDisposable
{
    private const string authCookie = CookieAuthenticationDefaults.CookiePrefix + CookieAuthenticationDefaults.AuthenticationScheme;

    private readonly string _keysDirectory;
    private readonly IDataProtectionProvider _dataProtection;

    public CookiePolicyCookieAuthenticationTests()
    {
        _keysDirectory = Path.Combine(Path.GetTempPath(), "cohesion-cookie-policy-tests", Guid.NewGuid().ToString("N"));
        _dataProtection = DataProtectionProvider.Create(KeyRepository.CreateFileSystem(_keysDirectory));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_keysDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Cookie auth: Without consent the sign-in cookie should still be issued, Secure behind a TLS proxy")]
    public async Task SignIn_ConsentRequiredAndAbsentBehindTlsProxy_ShouldIssueTheHardenedAuthCookie()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = CreateFactory(
            policy =>
            {
                policy.CheckConsentNeeded = _ => true;
                policy.OnRejected = recorder.Record;
            },
            handler: async context =>
            {
                await context.SignInAsync(CreatePrincipal("alice"), cancellationToken: context.RequestCancelled);
                context.Response.Cookies.Add(new HttpCookie("tracking", "1"));
            });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory, CookiePolicyTestHost.ForwardedHttps);

        // Assert — the ticket is issued with every hardening attribute; the tracking cookie is held back.
        SetCookieField ticket = fields.ShouldHaveSingleItem();
        ticket.Name.ShouldBe(authCookie);
        ticket.Value.ShouldNotBeNullOrEmpty();
        ticket.Get("Path").ShouldBe("/");
        ticket.Has("Secure").ShouldBeTrue();
        ticket.Has("HttpOnly").ShouldBeTrue();
        ticket.Get("SameSite").ShouldBe("Lax");
        recorder.Rejections.ShouldHaveSingleItem().ShouldBe(("tracking", CookiePolicyRejectionReason.ConsentRequired));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Cookie auth: A signed-in session should round-trip through the policy without consent")]
    public async Task SignIn_ThenNextRequest_ShouldAuthenticateThroughThePolicy()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory(
            policy => policy.CheckConsentNeeded = _ => true,
            handler: async context =>
            {
                if (context.Request.Path.Value == "/login")
                {
                    await context.SignInAsync(CreatePrincipal("alice"), cancellationToken: context.RequestCancelled);
                    return;
                }

                string name = context.User?.Identity is { IsAuthenticated: true } identity
                    ? identity.Name ?? string.Empty
                    : "anonymous";
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(name), context.RequestCancelled);
            });

        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();

        // Act — the client's cookie store carries the ticket forward, as a browser would over http.
        await CookiePolicyTestHost.SendAsync(client, "/login", cancellation.Token);
        using HttpResponseMessage response = await client.GetAsync("/me", cancellation.Token);
        string body = await response.Content.ReadAsStringAsync(cancellation.Token);

        // Assert
        body.ShouldBe("alice");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Cookie auth: A minimum SameSite of Strict should raise the sign-in cookie")]
    public async Task SignIn_MinimumSameSiteStrict_ShouldRaiseTheAuthCookie()
    {
        // Arrange
        await using WebApplicationTestFactory factory = CreateFactory(
            policy => policy.MinimumSameSitePolicy = HttpCookieSameSiteMode.Strict,
            handler: context => context.SignInAsync(CreatePrincipal("alice"), cancellationToken: context.RequestCancelled));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Get("SameSite").ShouldBe("Strict");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Cookie auth: Without consent the sign-out deletion should still be issued")]
    public async Task SignOut_ConsentRequiredAndAbsent_ShouldIssueTheDeletion()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = CreateFactory(
            policy =>
            {
                policy.CheckConsentNeeded = _ => true;
                policy.OnRejected = recorder.Record;
            },
            handler: context => context.SignOutAsync(cancellationToken: context.RequestCancelled));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory, CookiePolicyTestHost.ForwardedHttps);

        // Assert
        SetCookieField deletion = fields.ShouldHaveSingleItem();
        deletion.Name.ShouldBe(authCookie);
        deletion.Value.ShouldBeEmpty();
        deletion.Get("Max-Age").ShouldBe("0");
        deletion.Has("Secure").ShouldBeTrue();
        recorder.Rejections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Cookie auth: A __Host- ticket name should be issued compliant over https and rejected over http")]
    public async Task SignIn_HostPrefixedCookieNameUnderReject_ShouldComposeWithTheSecureFloor()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = CreateFactory(
            policy =>
            {
                policy.PrefixViolation = CookieViolationAction.Reject;
                policy.OnRejected = recorder.Record;
            },
            handler: context => context.SignInAsync(CreatePrincipal("alice"), cancellationToken: context.RequestCancelled),
            cookie: options => options.CookieName = "__Host-auth");

        // Act
        IReadOnlyList<SetCookieField> overHttps = await SendAsync(factory, CookiePolicyTestHost.ForwardedHttps);
        IReadOnlyList<SetCookieField> overHttp = await SendAsync(factory);

        // Assert — the template's Path=/ and absent Domain plus the https floor satisfy __Host-.
        SetCookieField ticket = overHttps.ShouldHaveSingleItem();
        ticket.Name.ShouldBe("__Host-auth");
        ticket.Get("Path").ShouldBe("/");
        ticket.Has("Secure").ShouldBeTrue();
        ticket.Has("Domain").ShouldBeFalse();
        overHttp.ShouldBeEmpty();
        recorder.Rejections.ShouldHaveSingleItem().ShouldBe(("__Host-auth", CookiePolicyRejectionReason.HostPrefixViolation));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Cookie auth: A sign-in cookie the application marks non-essential should wait for consent")]
    public async Task SignIn_AuthCookieMarkedNonEssential_ShouldBeHeldBackWithoutConsent()
    {
        // Arrange
        RejectionRecorder recorder = new();
        await using WebApplicationTestFactory factory = CreateFactory(
            policy =>
            {
                policy.CheckConsentNeeded = _ => true;
                policy.OnRejected = recorder.Record;
            },
            handler: context => context.SignInAsync(CreatePrincipal("alice"), cancellationToken: context.RequestCancelled),
            cookie: options => options.Cookie.IsEssential = false);

        // Act
        IReadOnlyList<SetCookieField> withoutConsent = await SendAsync(factory);
        IReadOnlyList<SetCookieField> withConsent = await SendAsync(factory, ("Cookie", $"{CookiePolicyOptions.DefaultConsentCookieName}=yes"));

        // Assert
        withoutConsent.ShouldBeEmpty();
        recorder.Rejections.ShouldHaveSingleItem().ShouldBe((authCookie, CookiePolicyRejectionReason.ConsentRequired));
        withConsent.ShouldHaveSingleItem().Name.ShouldBe(authCookie);
    }

    private WebApplicationTestFactory CreateFactory(
        Action<CookiePolicyOptions> policy,
        Func<IHttpContext, Task> handler,
        Action<CookieAuthenticationOptions>? cookie = null)
    {
        WebApplicationTestFactory factory = new();

        factory.Builder.Services.AddAuthentication(authentication =>
        {
            authentication.Options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            authentication.UseDataProtection(_dataProtection).AddCookie(cookie);
        });

        factory.Application.UseForwardedHeaders(options => options.Headers = ForwardedHeaderNames.XForwarded);
        factory.Application.UseCookiePolicy(policy);
        factory.Application.UseAuthentication();
        factory.Application.Use(async (context, next) =>
        {
            context.Response.StatusCode = HttpStatusCode.Ok;
            await handler(context);
        });

        return factory;
    }

    private static ClaimsPrincipal CreatePrincipal(string name)
    {
        ClaimsIdentity identity = new(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        return new ClaimsPrincipal(identity);
    }

    private static async Task<IReadOnlyList<SetCookieField>> SendAsync(
        WebApplicationTestFactory factory,
        params (string Name, string Value)[] headers)
    {
        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();
        return await CookiePolicyTestHost.SendAsync(client, cancellation.Token, headers);
    }
}
