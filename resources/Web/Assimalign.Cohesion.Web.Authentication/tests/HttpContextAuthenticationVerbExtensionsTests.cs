using System;
using System.Security.Claims;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Authentication.Tests;

public class HttpContextAuthenticationVerbExtensionsTests
{
    private static TestHttpContext CreateContextWithScheme(string scheme, RecordingAuthenticationHandler handler)
    {
        AuthenticationOptions options = new() { DefaultScheme = scheme };
        options.AddScheme(new AuthenticationScheme(scheme, null, () => handler));
        IAuthenticationService service = AuthenticationService.Create(options);

        TestHttpContext context = TestHttpContext.Create();
        context.Features.Set<IAuthenticationService>(service);
        return context;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - context.AuthenticateAsync: throws when no service is installed")]
    public async Task AuthenticateAsync_NoService_Throws()
    {
        // Arrange
        TestHttpContext context = TestHttpContext.Create();

        // Act + Assert
        await Should.ThrowAsync<InvalidOperationException>(() => context.AuthenticateAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - context.ChallengeAsync: dispatches through the installed service")]
    public async Task ChallengeAsync_ServiceInstalled_Dispatches()
    {
        // Arrange
        RecordingAuthenticationHandler handler = new();
        TestHttpContext context = CreateContextWithScheme("Test", handler);

        // Act
        await context.ChallengeAsync();

        // Assert
        handler.ChallengeCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication] - context.SignInAsync: dispatches the principal through the service")]
    public async Task SignInAsync_ServiceInstalled_Dispatches()
    {
        // Arrange
        RecordingAuthenticationHandler handler = new();
        TestHttpContext context = CreateContextWithScheme("Test", handler);
        ClaimsPrincipal principal = new(new ClaimsIdentity("Test"));

        // Act
        await context.SignInAsync(principal);

        // Assert
        handler.SignedInUser.ShouldBeSameAs(principal);
    }
}
