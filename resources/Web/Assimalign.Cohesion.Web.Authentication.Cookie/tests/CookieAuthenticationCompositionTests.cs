using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Authentication.Cookie.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Authentication.Cookie.Tests;

public sealed class CookieAuthenticationCompositionTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - AddAuthentication: Invalid cookie name fails assignment and registers nothing")]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAuthentication_InvalidCookieName_ShouldThrowBeforeRegistration(bool namedScheme)
    {
        // Arrange
        ServiceProviderBuilder services = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => services.AddAuthentication(auth =>
        {
            Action<CookieAuthenticationOptions> configure = options => options.CookieName = "bad name";
            if (namedScheme)
            {
                auth.AddCookie("custom", configure);
            }
            else
            {
                auth.AddCookie(configure);
            }
        }));

        // Assert
        exception.ParamName.ShouldBe("value");
        services.Container.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - AddAuthentication: Default cookie name fails composition and registers nothing")]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAuthentication_DefaultCookieName_ShouldThrowBeforeRegistration(bool namedScheme)
    {
        // Arrange
        ServiceProviderBuilder services = new();
        AuthenticationBuilder? captured = null;

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => services.AddAuthentication(auth =>
        {
            captured = auth;
            Action<CookieAuthenticationOptions> configure = options => options.CookieName = default;
            if (namedScheme)
            {
                auth.AddCookie("custom", configure);
            }
            else
            {
                auth.AddCookie(configure);
            }
        }));

        // Assert
        exception.ParamName.ShouldBe("options");
        exception.Message.ShouldStartWith("CookieAuthenticationOptions.CookieName must be a non-empty RFC 6265 token.", Case.Sensitive);
        exception.InnerException.ShouldBeNull();
        services.Container.ShouldBeEmpty();
        captured.ShouldNotBeNull().Options.Schemes.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - AddAuthentication: Typed cookie name reaches sign-in unchanged")]
    public async Task AddAuthentication_TypedCookieName_ShouldPreserveNameAtSignIn()
    {
        // Arrange
        ServiceProviderBuilder services = new();
        HttpCookieName name = "!Custom.Cookie+Name";
        AuthenticationBuilder? captured = null;
        services.AddAuthentication(auth =>
        {
            captured = auth;
            auth.AddCookie("custom", options =>
            {
                options.CookieName = name;
                options.TicketProtector = new DiagnosticDataProtector();
            });
        });
        AuthenticationScheme scheme = captured.ShouldNotBeNull().Options.GetScheme("custom").ShouldNotBeNull();
        IAuthenticationSignInHandler handler = scheme.CreateHandler().ShouldBeAssignableTo<IAuthenticationSignInHandler>();
        TestHttpContext context = TestHttpContext.Create();
        await handler.InitializeAsync(scheme, context, CancellationToken.None);

        // Act
        await handler.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity("custom")), null, CancellationToken.None);

        // Assert
        context.Response.Cookies.ShouldHaveSingleItem().Name.ShouldBe(name);
        services.Container.ShouldHaveSingleItem();
    }
}
