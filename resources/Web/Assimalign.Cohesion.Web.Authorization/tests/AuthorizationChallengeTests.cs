using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authentication.Bearer;
using Assimalign.Cohesion.Web.Authentication.Cookie;
using Assimalign.Cohesion.Web.Authorization.Tests.TestObjects;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Testing;

using CohesionHttpMethod = Assimalign.Cohesion.Http.HttpMethod;
using CohesionHttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;
using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Authorization.Tests;

/// <summary>
/// Challenge and forbid through the shipped handlers, end to end: JWT Bearer answers <c>401</c> with an
/// RFC 6750 challenge and <c>403</c> with <c>insufficient_scope</c>; Cookie redirects a browser endpoint to
/// its login or access-denied page and answers an API endpoint with a bare <c>401</c>; and an endpoint that
/// selects the Bearer scheme ignores the cookie the default scheme accepted.
/// </summary>
public sealed class AuthorizationChallengeTests : IDisposable
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] _signingKey = Encoding.UTF8.GetBytes("an-authorization-test-hmac-signing-key-256!");

    private readonly string _keysDirectory;
    private readonly IDataProtectionProvider _dataProtection;

    public AuthorizationChallengeTests()
    {
        _keysDirectory = Path.Combine(Path.GetTempPath(), "cohesion-web-authorization-tests", Guid.NewGuid().ToString("N"));
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

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Bearer: An anonymous request should be challenged with 401 and a Bearer challenge")]
    public async Task UseAuthorization_BearerAnonymous_ShouldAnswer401WithBearerChallenge()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(JwtBearerDefaults.AuthenticationScheme);

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/admin", TestEndpoints.Ok()).RequireAuthorization(policy => policy.RequireRole("admin"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/admin", cancellation.Token);

        // Assert — RFC 9110 §15.5.2 / RFC 6750 §3: the 401 carries a Bearer challenge.
        response.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("Bearer");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Bearer: A valid token without the required role should be forbidden with insufficient_scope")]
    public async Task UseAuthorization_BearerTokenMissingRole_ShouldAnswer403InsufficientScope()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(JwtBearerDefaults.AuthenticationScheme);

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/admin", TestEndpoints.Ok()).RequireAuthorization(policy => policy.RequireRole("admin"));

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/admin");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.Create(_signingKey, "alice", "reader"));

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert — RFC 6750 §3.1.
        response.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        AuthenticationHeaderValue challenge = response.Headers.WwwAuthenticate.ShouldHaveSingleItem();
        challenge.Scheme.ShouldBe("Bearer");
        challenge.Parameter.ShouldNotBeNull().ShouldContain("insufficient_scope", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Bearer: A valid token in the required role should reach the endpoint")]
    public async Task UseAuthorization_BearerTokenInRole_ShouldRunTheEndpoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(JwtBearerDefaults.AuthenticationScheme);

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        routes.Map(CohesionHttpMethod.Get, "/admin", TestEndpoints.Ok()).RequireAuthorization(policy => policy.RequireRole("admin"));

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, "/admin");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.Create(_signingKey, "alice", "admin"));

        // Act
        using HttpResponseMessage response = await client.SendAsync(request, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("ok:alice");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Cookie: An anonymous browser request should be redirected to the login page")]
    public async Task UseAuthorization_CookieAnonymousBrowserEndpoint_ShouldRedirectToLogin()
    {
        // Arrange — the client follows the redirect to the (anonymous) login page.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(CookieAuthenticationDefaults.AuthenticationScheme);
        StrongBox<int> invocations = new();

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        MapAccountPages(routes);
        routes.Map(CohesionHttpMethod.Get, "/admin", TestEndpoints.Ok(invocations)).RequireAuthorization();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/admin", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe(CookieAuthenticationDefaults.LoginPath);
        response.RequestMessage.RequestUri.Query.ShouldBe("?ReturnUrl=%2Fadmin");
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("login-page");
        invocations.Value.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Cookie: An anonymous API request should be answered with a bare 401")]
    public async Task UseAuthorization_CookieAnonymousApiEndpoint_ShouldAnswer401()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(CookieAuthenticationDefaults.AuthenticationScheme);

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        MapAccountPages(routes);
        routes.Map(CohesionHttpMethod.Get, "/api/orders", TestEndpoints.Ok())
            .WithMetadata(ApiEndpointMetadata.Instance)
            .RequireAuthorization();

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage response = await client.GetAsync("/api/orders", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/api/orders");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Cookie: A signed-in user without the required role should be redirected to the access-denied page")]
    public async Task UseAuthorization_CookieUserMissingRole_ShouldRedirectToAccessDenied()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(CookieAuthenticationDefaults.AuthenticationScheme);

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        MapAccountPages(routes);
        routes.Map(CohesionHttpMethod.Get, "/admin", TestEndpoints.Ok()).RequireAuthorization(policy => policy.RequireRole("admin"));

        using HttpClient client = factory.CreateClient();

        // Act
        using HttpResponseMessage signIn = await client.GetAsync("/sign-in", cancellation.Token);
        using HttpResponseMessage response = await client.GetAsync("/admin", cancellation.Token);

        // Assert
        signIn.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe(CookieAuthenticationDefaults.AccessDeniedPath);
        (await response.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("access-denied");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authorization] - Schemes: An endpoint that selects Bearer should ignore the cookie the default scheme accepted")]
    public async Task RequireAuthorization_BearerSchemeWithCookieDefault_ShouldIgnoreTheCookie()
    {
        // Arrange — Cookies is the default scheme; the API endpoint selects Bearer.
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using WebApplicationTestFactory factory = CreateFactory(CookieAuthenticationDefaults.AuthenticationScheme);

        IRouterBuilder routes = UseAuthorizedRouting(factory);
        MapAccountPages(routes);
        routes.Map(CohesionHttpMethod.Get, "/api/data", TestEndpoints.Ok())
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireRole("admin"));

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage withToken = new(System.Net.Http.HttpMethod.Get, "/api/data");
        withToken.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.Create(_signingKey, "bob", "admin"));

        // Act — the signed-in cookie user is an admin, but the endpoint only accepts Bearer.
        using HttpResponseMessage signIn = await client.GetAsync("/sign-in?role=admin", cancellation.Token);
        using HttpResponseMessage cookieOnly = await client.GetAsync("/api/data", cancellation.Token);
        using HttpResponseMessage bearer = await client.SendAsync(withToken, cancellation.Token);

        // Assert
        signIn.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        cookieOnly.StatusCode.ShouldBe(NetHttpStatusCode.Unauthorized);
        cookieOnly.Headers.WwwAuthenticate.ShouldHaveSingleItem().Scheme.ShouldBe("Bearer");
        bearer.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        (await bearer.Content.ReadAsStringAsync(cancellation.Token)).ShouldBe("ok:bob");
    }

    private WebApplicationTestFactory CreateFactory(string defaultScheme)
    {
        WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddRouting();
        factory.Builder.Services.AddAuthentication(authentication =>
        {
            authentication.Options.DefaultScheme = defaultScheme;
            authentication
                .UseDataProtection(_dataProtection)
                .AddCookie()
                .AddJwtBearer(options =>
                {
                    options.SigningKeys.Add(JwtSignatureVerifier.CreateHmac(_signingKey));
                    options.ValidIssuers.Add(TestJwt.Issuer);
                    options.ValidAudiences.Add(TestJwt.Audience);
                });
        });
        factory.Builder.Services.AddAuthorization();
        return factory;
    }

    private static IRouterBuilder UseAuthorizedRouting(WebApplicationTestFactory factory)
    {
        IRouterBuilder routes = factory.Application.UseRouting();
        factory.Application.UseAuthentication();
        factory.Application.UseAuthorization();
        return routes;
    }

    // The cookie scheme's login and access-denied pages, and a sign-in endpoint that issues the ticket
    // cookie for "alice" (in the role named by the query, when one is).
    private static void MapAccountPages(IRouterBuilder routes)
    {
        routes.Map(CohesionHttpMethod.Get, CookieAuthenticationDefaults.LoginPath, Page("login-page")).AllowAnonymous();
        routes.Map(CohesionHttpMethod.Get, CookieAuthenticationDefaults.AccessDeniedPath, Page("access-denied")).AllowAnonymous();
        routes.Map(CohesionHttpMethod.Get, "/sign-in", new RouterRouteHandler(async context =>
        {
            ClaimsIdentity identity = new(CookieAuthenticationDefaults.AuthenticationScheme);
            identity.AddClaim(new Claim(ClaimTypes.Name, "alice"));

            if (context.Request.Query.TryGetValue("role", out HttpQueryValue role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role.Value));
            }

            await context.SignInAsync(new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme, cancellationToken: context.RequestCancelled);
            context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        })).AllowAnonymous();
    }

    private static RouterRouteHandler Page(string body) => new(async context =>
    {
        context.Response.StatusCode = CohesionHttpStatusCode.Ok;
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body), context.RequestCancelled);
    });
}
