using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Authentication.Cookie.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Authentication.Cookie.Tests;

public sealed class CookieAuthenticationHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly string _keysDirectory;
    private readonly IDataProtector _protector;
    private readonly MutableTimeProvider _time = new(Now);

    public CookieAuthenticationHandlerTests()
    {
        _keysDirectory = Path.Combine(Path.GetTempPath(), "cohesion-cookie-tests", Guid.NewGuid().ToString("N"));
        IDataProtectionProvider provider = DataProtectionProvider.Create(KeyRepository.CreateFileSystem(_keysDirectory));
        _protector = provider.CreateProtector("cookie-tests");
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

    private CookieAuthenticationOptions CreateOptions()
        => new() { TicketProtector = _protector, TimeProvider = _time };

    private static async Task<IAuthenticationSignInHandler> InitializeAsync(
        CookieAuthenticationOptions options, TestHttpContext context)
    {
        IAuthenticationSignInHandler handler = CookieAuthentication.CreateHandler(options);
        AuthenticationScheme scheme = new(CookieAuthenticationDefaults.AuthenticationScheme, null, () => handler);
        await handler.InitializeAsync(scheme, context);
        return handler;
    }

    private static ClaimsPrincipal CreatePrincipal(string name)
    {
        ClaimsIdentity identity = new("Cookies");
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        return new ClaimsPrincipal(identity);
    }

    private static string GetEmittedCookieValue(TestHttpContext context, string name)
    {
        foreach (HttpCookie cookie in context.Response.Cookies)
        {
            if (string.Equals(cookie.Name, name, StringComparison.Ordinal))
            {
                return cookie.Value;
            }
        }

        throw new InvalidOperationException($"No emitted cookie named '{name}'.");
    }

    private static HttpCookie GetEmittedCookie(TestHttpContext context, string name)
    {
        foreach (HttpCookie cookie in context.Response.Cookies)
        {
            if (string.Equals(cookie.Name, name, StringComparison.Ordinal))
            {
                return cookie;
            }
        }

        throw new InvalidOperationException($"No emitted cookie named '{name}'.");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - SignIn then Authenticate round-trips the principal")]
    public async Task SignIn_ThenAuthenticate_RoundTripsPrincipal()
    {
        // Arrange
        CookieAuthenticationOptions options = CreateOptions();

        TestHttpContext signInContext = TestHttpContext.Create();
        IAuthenticationSignInHandler signInHandler = await InitializeAsync(options, signInContext);
        await signInHandler.SignInAsync(CreatePrincipal("alice"), properties: null);
        string cookieValue = GetEmittedCookieValue(signInContext, options.CookieName);

        // Act — replay the cookie on a fresh request.
        TestHttpContext authContext = TestHttpContext.Create();
        authContext.Request.Cookies.Add(new HttpCookie(options.CookieName, cookieValue));
        IAuthenticationSignInHandler authHandler = await InitializeAsync(options, authContext);
        AuthenticateResult result = await authHandler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Principal!.Identity!.Name.ShouldBe("alice");
        result.Principal.IsInRole("admin").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - SignIn emits an HttpOnly cookie")]
    public async Task SignIn_EmitsHardenedCookie()
    {
        // Arrange
        CookieAuthenticationOptions options = CreateOptions();
        options.Cookie.Secure = true;
        TestHttpContext context = TestHttpContext.Create();
        IAuthenticationSignInHandler handler = await InitializeAsync(options, context);

        // Act
        await handler.SignInAsync(CreatePrincipal("alice"), properties: null);

        // Assert
        HttpCookie cookie = GetEmittedCookie(context, options.CookieName);
        cookie.Options.HttpOnly.ShouldBeTrue();
        cookie.Options.Secure.ShouldBeTrue();
        cookie.Options.SameSite.ShouldBe(HttpCookieSameSiteMode.Lax);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Authenticate with no cookie yields NoResult")]
    public async Task Authenticate_NoCookie_NoResult()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create();
        IAuthenticationSignInHandler handler = await InitializeAsync(CreateOptions(), context);

        // Act
        AuthenticateResult result = await handler.AuthenticateAsync();

        // Assert
        result.None.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Authenticate with a tampered cookie fails")]
    public async Task Authenticate_TamperedCookie_Fails()
    {
        // Arrange
        CookieAuthenticationOptions options = CreateOptions();
        TestHttpContext signInContext = TestHttpContext.Create();
        IAuthenticationSignInHandler signInHandler = await InitializeAsync(options, signInContext);
        await signInHandler.SignInAsync(CreatePrincipal("alice"), properties: null);
        string cookieValue = GetEmittedCookieValue(signInContext, options.CookieName);

        // Flip the final character to break the authentication tag.
        char[] chars = cookieValue.ToCharArray();
        chars[^1] = chars[^1] == 'A' ? 'B' : 'A';
        string tampered = new(chars);

        TestHttpContext authContext = TestHttpContext.Create();
        authContext.Request.Cookies.Add(new HttpCookie(options.CookieName, tampered));
        IAuthenticationSignInHandler authHandler = await InitializeAsync(options, authContext);

        // Act
        AuthenticateResult result = await authHandler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.None.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Authenticate with an expired ticket fails")]
    public async Task Authenticate_ExpiredTicket_Fails()
    {
        // Arrange
        CookieAuthenticationOptions options = CreateOptions();
        options.ExpireTimeSpan = TimeSpan.FromHours(1);

        TestHttpContext signInContext = TestHttpContext.Create();
        IAuthenticationSignInHandler signInHandler = await InitializeAsync(options, signInContext);
        await signInHandler.SignInAsync(CreatePrincipal("alice"), properties: null);
        string cookieValue = GetEmittedCookieValue(signInContext, options.CookieName);

        // Advance beyond the ticket lifetime.
        _time.Advance(TimeSpan.FromHours(2));

        TestHttpContext authContext = TestHttpContext.Create();
        authContext.Request.Cookies.Add(new HttpCookie(options.CookieName, cookieValue));
        IAuthenticationSignInHandler authHandler = await InitializeAsync(options, authContext);

        // Act
        AuthenticateResult result = await authHandler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Sliding expiration renews the cookie past the midpoint")]
    public async Task Authenticate_PastMidpoint_RenewsCookie()
    {
        // Arrange
        CookieAuthenticationOptions options = CreateOptions();
        options.ExpireTimeSpan = TimeSpan.FromHours(10);
        options.SlidingExpiration = true;

        TestHttpContext signInContext = TestHttpContext.Create();
        IAuthenticationSignInHandler signInHandler = await InitializeAsync(options, signInContext);
        await signInHandler.SignInAsync(CreatePrincipal("alice"), new AuthenticationProperties { IsPersistent = true });
        string cookieValue = GetEmittedCookieValue(signInContext, options.CookieName);

        // Past the midpoint of the 10-hour window.
        _time.Advance(TimeSpan.FromHours(6));

        TestHttpContext authContext = TestHttpContext.Create();
        authContext.Request.Cookies.Add(new HttpCookie(options.CookieName, cookieValue));
        IAuthenticationSignInHandler authHandler = await InitializeAsync(options, authContext);

        // Act
        AuthenticateResult result = await authHandler.AuthenticateAsync();

        // Assert — a fresh Set-Cookie is emitted on the authenticated response.
        result.Succeeded.ShouldBeTrue();
        HttpCookie renewed = GetEmittedCookie(authContext, options.CookieName);
        renewed.Options.Expires!.Value.ShouldBe(_time.UtcNow + options.ExpireTimeSpan);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Sliding expiration does not renew before the midpoint")]
    public async Task Authenticate_BeforeMidpoint_DoesNotRenew()
    {
        // Arrange
        CookieAuthenticationOptions options = CreateOptions();
        options.ExpireTimeSpan = TimeSpan.FromHours(10);

        TestHttpContext signInContext = TestHttpContext.Create();
        IAuthenticationSignInHandler signInHandler = await InitializeAsync(options, signInContext);
        await signInHandler.SignInAsync(CreatePrincipal("alice"), properties: null);
        string cookieValue = GetEmittedCookieValue(signInContext, options.CookieName);

        _time.Advance(TimeSpan.FromHours(1));

        TestHttpContext authContext = TestHttpContext.Create();
        authContext.Request.Cookies.Add(new HttpCookie(options.CookieName, cookieValue));
        IAuthenticationSignInHandler authHandler = await InitializeAsync(options, authContext);

        // Act
        AuthenticateResult result = await authHandler.AuthenticateAsync();

        // Assert — no renewal cookie was written.
        result.Succeeded.ShouldBeTrue();
        authContext.Response.Cookies.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Challenge on a browser endpoint redirects to the login path")]
    public async Task Challenge_BrowserEndpoint_RedirectsToLogin()
    {
        // Arrange
        CookieAuthenticationOptions options = CreateOptions();
        TestHttpContext context = TestHttpContext.Create("/secure/data");
        IAuthenticationSignInHandler handler = await InitializeAsync(options, context);

        // Act
        await handler.ChallengeAsync(properties: null);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(302);
        string? location = context.Response.Headers.GetValue(HttpHeaderKey.Location);
        location.ShouldNotBeNull();
        location!.ShouldStartWith(options.LoginPath);
        location.ShouldContain("ReturnUrl=");
        location.ShouldContain(Uri.EscapeDataString("/secure/data"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Challenge on an API endpoint returns 401 without redirect")]
    public async Task Challenge_ApiEndpoint_Returns401()
    {
        // Arrange
        CookieAuthenticationOptions options = CreateOptions();
        TestHttpContext context = TestHttpContext.Create("/api/data");
        context.MarkApiEndpoint();
        IAuthenticationSignInHandler handler = await InitializeAsync(options, context);

        // Act
        await handler.ChallengeAsync(properties: null);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(401);
        context.Response.Headers.GetValue(HttpHeaderKey.Location).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Forbid on an API endpoint returns 403 without redirect")]
    public async Task Forbid_ApiEndpoint_Returns403()
    {
        // Arrange
        CookieAuthenticationOptions options = CreateOptions();
        TestHttpContext context = TestHttpContext.Create("/api/data");
        context.MarkApiEndpoint();
        IAuthenticationSignInHandler handler = await InitializeAsync(options, context);

        // Act
        await handler.ForbidAsync(properties: null);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(403);
        context.Response.Headers.GetValue(HttpHeaderKey.Location).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Forbid on a browser endpoint redirects to access denied")]
    public async Task Forbid_BrowserEndpoint_RedirectsToAccessDenied()
    {
        // Arrange
        CookieAuthenticationOptions options = CreateOptions();
        TestHttpContext context = TestHttpContext.Create("/secure/data");
        IAuthenticationSignInHandler handler = await InitializeAsync(options, context);

        // Act
        await handler.ForbidAsync(properties: null);

        // Assert
        context.Response.StatusCode.Value.ShouldBe(302);
        context.Response.Headers.GetValue(HttpHeaderKey.Location)!.ShouldStartWith(options.AccessDeniedPath);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - SignOut deletes the cookie")]
    public async Task SignOut_EmitsDeletionCookie()
    {
        // Arrange
        CookieAuthenticationOptions options = CreateOptions();
        TestHttpContext context = TestHttpContext.Create();
        IAuthenticationSignInHandler handler = await InitializeAsync(options, context);

        // Act
        await handler.SignOutAsync(properties: null);

        // Assert — the deletion cookie has an empty value and a past expiry.
        HttpCookie cookie = GetEmittedCookie(context, options.CookieName);
        cookie.HasValue.ShouldBeFalse();
        cookie.Options.Expires!.Value.ShouldBe(DateTimeOffset.UnixEpoch);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - A non-persistent sign-in emits a session cookie")]
    public async Task SignIn_NonPersistent_EmitsSessionCookie()
    {
        // Arrange
        CookieAuthenticationOptions options = CreateOptions();
        TestHttpContext context = TestHttpContext.Create();
        IAuthenticationSignInHandler handler = await InitializeAsync(options, context);

        // Act
        await handler.SignInAsync(CreatePrincipal("alice"), new AuthenticationProperties { IsPersistent = false });

        // Assert — no Expires/Max-Age means the browser drops it at session end.
        HttpCookie cookie = GetEmittedCookie(context, options.CookieName);
        cookie.Options.Expires.ShouldBeNull();
        cookie.Options.MaxAge.ShouldBeNull();
    }
}
