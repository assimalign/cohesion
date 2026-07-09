using System;
using System.Security.Claims;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Authentication.Tests;

public class AuthenticationServiceTests
{
    private static (IAuthenticationService service, AuthenticationOptions options) CreateService(
        Action<AuthenticationOptions> configure)
    {
        AuthenticationOptions options = new();
        configure(options);
        return (AuthenticationService.Create(options), options);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticateAsync: dispatches to the default scheme handler")]
    public async Task AuthenticateAsync_DefaultScheme_DispatchesToHandler()
    {
        // Arrange
        RecordingAuthenticationHandler handler = new(AuthenticateResult.NoResult());
        var (service, _) = CreateService(o =>
        {
            o.DefaultScheme = "Test";
            o.AddScheme(new AuthenticationScheme("Test", null, () => handler));
        });
        TestHttpContext context = TestHttpContext.Create();

        // Act
        AuthenticateResult result = await service.AuthenticateAsync(context, scheme: null);

        // Assert
        handler.AuthenticateCount.ShouldBe(1);
        handler.InitializeCount.ShouldBe(1);
        handler.Scheme!.Name.ShouldBe("Test");
        result.None.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticateAsync: reuses the per-request handler instance")]
    public async Task AuthenticateAsync_CalledTwice_ReusesHandler()
    {
        // Arrange
        RecordingAuthenticationHandler handler = new();
        var (service, _) = CreateService(o =>
        {
            o.DefaultScheme = "Test";
            o.AddScheme(new AuthenticationScheme("Test", null, () => handler));
        });
        TestHttpContext context = TestHttpContext.Create();

        // Act
        await service.AuthenticateAsync(context, "Test");
        await service.ChallengeAsync(context, "Test", properties: null);

        // Assert — the handler was initialized once and reused for both verbs.
        handler.InitializeCount.ShouldBe(1);
        handler.AuthenticateCount.ShouldBe(1);
        handler.ChallengeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticateAsync: records the default result on the result feature")]
    public async Task AuthenticateAsync_DefaultScheme_InstallsResultFeature()
    {
        // Arrange
        ClaimsPrincipal principal = new(new ClaimsIdentity("Test"));
        AuthenticationTicket ticket = new(principal, null, "Test");
        RecordingAuthenticationHandler handler = new(AuthenticateResult.Success(ticket));
        var (service, _) = CreateService(o =>
        {
            o.DefaultScheme = "Test";
            o.AddScheme(new AuthenticationScheme("Test", null, () => handler));
        });
        TestHttpContext context = TestHttpContext.Create();

        // Act
        await service.AuthenticateAsync(context, scheme: null);

        // Assert
        IAuthenticationResultFeature? feature = context.Features.Get<IAuthenticationResultFeature>();
        feature.ShouldNotBeNull();
        feature!.AuthenticateResult!.Succeeded.ShouldBeTrue();
        feature.AuthenticateResult.Principal.ShouldBeSameAs(principal);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - ChallengeAsync: uses the default challenge scheme")]
    public async Task ChallengeAsync_UsesDefaultChallengeScheme()
    {
        // Arrange
        RecordingAuthenticationHandler cookie = new();
        RecordingAuthenticationHandler bearer = new();
        var (service, _) = CreateService(o =>
        {
            o.DefaultScheme = "Cookie";
            o.DefaultChallengeScheme = "Bearer";
            o.AddScheme(new AuthenticationScheme("Cookie", null, () => cookie));
            o.AddScheme(new AuthenticationScheme("Bearer", null, () => bearer));
        });
        TestHttpContext context = TestHttpContext.Create();

        // Act
        await service.ChallengeAsync(context, scheme: null, properties: null);

        // Assert
        bearer.ChallengeCount.ShouldBe(1);
        cookie.ChallengeCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - SignInAsync: throws when the scheme handler cannot sign in")]
    public async Task SignInAsync_NonSignInHandler_Throws()
    {
        // Arrange
        var (service, _) = CreateService(o =>
        {
            o.DefaultSignInScheme = "Bearer";
            o.AddScheme(new AuthenticationScheme("Bearer", null, () => new AuthenticateOnlyHandler()));
        });
        TestHttpContext context = TestHttpContext.Create();
        ClaimsPrincipal principal = new(new ClaimsIdentity("Test"));

        // Act + Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => service.SignInAsync(context, scheme: null, principal, properties: null));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - SignInAsync: dispatches to the sign-in handler")]
    public async Task SignInAsync_SignInHandler_Dispatches()
    {
        // Arrange
        RecordingAuthenticationHandler handler = new();
        var (service, _) = CreateService(o =>
        {
            o.DefaultSignInScheme = "Cookie";
            o.AddScheme(new AuthenticationScheme("Cookie", null, () => handler));
        });
        TestHttpContext context = TestHttpContext.Create();
        ClaimsPrincipal principal = new(new ClaimsIdentity("Test"));

        // Act
        await service.SignInAsync(context, scheme: null, principal, properties: null);

        // Assert
        handler.SignedInUser.ShouldBeSameAs(principal);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticateAsync: throws when the scheme is unknown")]
    public async Task AuthenticateAsync_UnknownScheme_Throws()
    {
        // Arrange
        var (service, _) = CreateService(o => o.DefaultScheme = "Missing");
        TestHttpContext context = TestHttpContext.Create();

        // Act + Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => service.AuthenticateAsync(context, scheme: null));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - AuthenticateAsync: throws when no default scheme is configured")]
    public async Task AuthenticateAsync_NoDefault_Throws()
    {
        // Arrange
        var (service, _) = CreateService(_ => { });
        TestHttpContext context = TestHttpContext.Create();

        // Act + Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => service.AuthenticateAsync(context, scheme: null));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - Service: resolves schemes registered after creation")]
    public async Task Service_SchemeAddedAfterCreate_IsResolved()
    {
        // Arrange — this is the composition-root pattern: the service is created first, schemes are
        // added onto the same options afterward.
        AuthenticationOptions options = new() { DefaultScheme = "Test" };
        IAuthenticationService service = AuthenticationService.Create(options);
        RecordingAuthenticationHandler handler = new();
        options.AddScheme(new AuthenticationScheme("Test", null, () => handler));
        TestHttpContext context = TestHttpContext.Create();

        // Act
        await service.AuthenticateAsync(context, scheme: null);

        // Assert
        handler.AuthenticateCount.ShouldBe(1);
    }
}
