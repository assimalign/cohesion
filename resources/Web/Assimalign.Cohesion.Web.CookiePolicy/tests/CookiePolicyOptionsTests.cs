using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.CookiePolicy.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.CookiePolicy.Tests;

/// <summary>
/// The options' documented defaults, and the validation <c>UseCookiePolicy</c> performs at registration:
/// a misconfiguration throws when the verb is called, never on a request.
/// </summary>
public class CookiePolicyOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Options: The defaults should be the documented secure defaults")]
    public void CookiePolicyOptions_Defaults_ShouldMatchTheDocumentedDefaults()
    {
        // Arrange & Act
        CookiePolicyOptions options = new();

        // Assert
        options.MinimumSameSitePolicy.ShouldBe(HttpCookieSameSiteMode.Unspecified);
        options.Secure.ShouldBe(CookieSecurePolicy.SameAsRequest);
        options.HttpOnly.ShouldBe(CookieHttpOnlyPolicy.None);
        options.SameSiteNoneWithoutSecure.ShouldBe(CookieViolationAction.Upgrade);
        options.PrefixViolation.ShouldBe(CookieViolationAction.Upgrade);
        options.MaxLifetime.ShouldBe(TimeSpan.FromDays(400));
        options.TimeProvider.ShouldBeSameAs(TimeProvider.System);
        options.CheckConsentNeeded.ShouldBeNull();
        options.OnRejected.ShouldBeNull();
        options.ConsentCookieName.ShouldBe(CookiePolicyOptions.DefaultConsentCookieName);
        options.ConsentCookieValue.ShouldBe(CookiePolicyOptions.DefaultConsentCookieValue);
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Options: The consent cookie template should default to an essential, script-readable, year-long Lax cookie")]
    public void ConsentCookie_Defaults_ShouldBeEssentialLaxAndReadableByScript()
    {
        // Arrange & Act
        HttpCookieOptions template = new CookiePolicyOptions().ConsentCookie;

        // Assert
        template.Path.ShouldBe("/");
        template.SameSite.ShouldBe(HttpCookieSameSiteMode.Lax);
        template.MaxAge.ShouldBe(TimeSpan.FromDays(365));
        template.Expires.ShouldBeNull();
        template.IsEssential.ShouldBeTrue();
        template.HttpOnly.ShouldBeFalse();
        template.Secure.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A lifetime cap outside (0, 400 days] should throw at registration")]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(400.0001d)]
    [InlineData(401d)]
    public async Task UseCookiePolicy_MaxLifetimeOutOfRange_ShouldThrowAtRegistration(double days)
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act & Assert
        ArgumentException exception = Should.Throw<ArgumentException>(() =>
            factory.Application.UseCookiePolicy(options => options.MaxLifetime = TimeSpan.FromDays(days)));
        exception.ParamName.ShouldBe("configure");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A shorter lifetime cap should be accepted")]
    public async Task UseCookiePolicy_MaxLifetimeShorterThanTheLimit_ShouldRegister()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act & Assert
        Should.NotThrow(() => factory.Application.UseCookiePolicy(options => options.MaxLifetime = TimeSpan.FromDays(30)));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A null time provider should throw at registration")]
    public async Task UseCookiePolicy_NullTimeProvider_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            factory.Application.UseCookiePolicy(options => options.TimeProvider = null!))
            .ParamName.ShouldBe("configure");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: Undefined enum values should throw at registration")]
    public async Task UseCookiePolicy_UndefinedEnumValues_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        Action<CookiePolicyOptions>[] misconfigurations =
        [
            options => options.MinimumSameSitePolicy = (HttpCookieSameSiteMode)42,
            options => options.Secure = (CookieSecurePolicy)42,
            options => options.HttpOnly = (CookieHttpOnlyPolicy)42,
            options => options.SameSiteNoneWithoutSecure = (CookieViolationAction)42,
            options => options.PrefixViolation = (CookieViolationAction)42,
        ];

        // Act & Assert
        foreach (Action<CookiePolicyOptions> misconfiguration in misconfigurations)
        {
            Should.Throw<ArgumentException>(() => factory.Application.UseCookiePolicy(misconfiguration))
                .ParamName.ShouldBe("configure");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A consent cookie name or value outside the RFC 6265 grammar should throw at registration")]
    [InlineData("bad name", "yes")]
    [InlineData("", "yes")]
    [InlineData("consent;", "yes")]
    [InlineData("consent", "")]
    [InlineData("consent", "a;b")]
    [InlineData("consent", "white space")]
    public async Task UseCookiePolicy_MalformedConsentCookie_ShouldThrowAtRegistration(string name, string value)
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() => factory.Application.UseCookiePolicy(options =>
        {
            options.ConsentCookieName = name;
            options.ConsentCookieValue = value;
        })).ParamName.ShouldBe("configure");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A consent cookie template that would delete consent should throw at registration")]
    public async Task UseCookiePolicy_ConsentTemplateWithNonPositiveMaxAge_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            factory.Application.UseCookiePolicy(options => options.ConsentCookie.MaxAge = TimeSpan.Zero));
        Should.Throw<ArgumentException>(() =>
            factory.Application.UseCookiePolicy(options => options.ConsentCookie.MaxAge = TimeSpan.FromSeconds(-1)));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A consent cookie template with a fixed Expires should throw at registration")]
    public async Task UseCookiePolicy_ConsentTemplateWithExpires_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() => factory.Application.UseCookiePolicy(options =>
            options.ConsentCookie.Expires = DateTimeOffset.UtcNow.AddDays(30)));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: Changing the options after registration should have no effect")]
    public async Task UseCookiePolicy_OptionsChangedAfterRegistration_ShouldKeepTheCapturedPolicy()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        CookiePolicyOptions? captured = null;
        factory.Application.UseCookiePolicy(options =>
        {
            options.HttpOnly = CookieHttpOnlyPolicy.Always;
            captured = options;
        });
        captured.ShouldNotBeNull().HttpOnly = CookieHttpOnlyPolicy.None;

        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("a", "1")));

        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();

        // Act
        IReadOnlyList<SetCookieField> fields = await CookiePolicyTestHost.SendAsync(client, cancellation.Token);

        // Assert
        fields.ShouldHaveSingleItem().Has("HttpOnly").ShouldBeTrue();
    }
}
