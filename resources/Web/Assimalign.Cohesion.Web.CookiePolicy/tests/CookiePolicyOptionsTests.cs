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
/// The options' documented defaults, assignment-time grammar validation, and the remaining validation
/// <c>builder.Services.AddCookiePolicy</c> performs at registration, before a request can grant consent.
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
        HttpCookieName consentCookieName = options.ConsentCookieName;
        HttpCookieValue consentCookieValue = options.ConsentCookieValue;
        consentCookieName.ShouldBe<HttpCookieName>(CookiePolicyOptions.DefaultConsentCookieName);
        consentCookieValue.ShouldBe<HttpCookieValue>(CookiePolicyOptions.DefaultConsentCookieValue);
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Options: The consent cookie template should default to an essential, script-readable, year-long Lax cookie")]
    public void ConsentCookie_Defaults_ShouldBeEssentialLaxAndReadableByScript()
    {
        // Arrange & Act
        HttpCookieOptions template = new CookiePolicyOptions().ConsentCookie;

        // Assert
        template.Path.ShouldBe<HttpCookiePath?>("/");
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
    public async Task AddCookiePolicy_MaxLifetimeOutOfRange_ShouldThrowAtRegistration(double days)
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act & Assert
        ArgumentException exception = Should.Throw<ArgumentException>(() =>
            factory.Builder.Services.AddCookiePolicy(policy => policy.Options.MaxLifetime = TimeSpan.FromDays(days)));
        exception.ParamName.ShouldBe("Options");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A shorter lifetime cap should be accepted")]
    public async Task AddCookiePolicy_MaxLifetimeShorterThanTheLimit_ShouldRegister()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act & Assert
        Should.NotThrow(() => factory.Builder.Services.AddCookiePolicy(policy => policy.Options.MaxLifetime = TimeSpan.FromDays(30)));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A null time provider should throw at registration")]
    public async Task AddCookiePolicy_NullTimeProvider_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            factory.Builder.Services.AddCookiePolicy(policy => policy.Options.TimeProvider = null!))
            .ParamName.ShouldBe("Options");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: Undefined enum values should throw at registration")]
    public async Task AddCookiePolicy_UndefinedEnumValues_ShouldThrowAtRegistration()
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
            Should.Throw<ArgumentException>(() => factory.Builder.Services.AddCookiePolicy(policy => misconfiguration(policy.Options)))
                .ParamName.ShouldBe("Options");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.CookiePolicy] - Options: A malformed consent cookie name should throw at assignment")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("consent;")]
    [InlineData("consent\r\nInjected")]
    [InlineData("café")]
    public void ConsentCookieName_MalformedString_ShouldThrowAtAssignment(string? name)
    {
        // Arrange
        CookiePolicyOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() => options.ConsentCookieName = name!);
    }

    [Theory(DisplayName = "Cohesion Test [Web.CookiePolicy] - Options: A malformed consent cookie value should throw at assignment")]
    [InlineData(null)]
    [InlineData("a;b")]
    [InlineData("white space")]
    [InlineData("yes\r\nInjected")]
    [InlineData("café")]
    public void ConsentCookieValue_MalformedString_ShouldThrowAtAssignment(string? value)
    {
        // Arrange
        CookiePolicyOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() => options.ConsentCookieValue = value!);
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A default consent cookie name should throw with the documented message")]
    public async Task AddCookiePolicy_DefaultConsentCookieName_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() =>
            factory.Builder.Services.AddCookiePolicy(policy => policy.Options.ConsentCookieName = default));

        // Assert
        exception.ParamName.ShouldBe("Options");
        exception.Message.ShouldBe(new ArgumentException(
            "The consent cookie name or value is not a valid RFC 6265 cookie name or value.",
            "Options").Message);
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A default consent cookie value should throw with the documented message")]
    public async Task AddCookiePolicy_DefaultConsentCookieValue_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() =>
            factory.Builder.Services.AddCookiePolicy(policy => policy.Options.ConsentCookieValue = default));

        // Assert
        exception.ParamName.ShouldBe("Options");
        exception.Message.ShouldBe(new ArgumentException(
            "The consent cookie value must not be empty: an empty value cannot be told apart from a deleted consent cookie.",
            "Options").Message);
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: An empty consent cookie value should be accepted at assignment and refused at registration")]
    public async Task AddCookiePolicy_EmptyConsentCookieValue_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        CookiePolicyOptions configured = new();
        Should.NotThrow(() => configured.ConsentCookieValue = string.Empty);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() =>
            factory.Builder.Services.AddCookiePolicy(policy => policy.Options.ConsentCookieValue = configured.ConsentCookieValue));

        // Assert
        exception.ParamName.ShouldBe("Options");
        exception.Message.ShouldBe(new ArgumentException(
            "The consent cookie value must not be empty: an empty value cannot be told apart from a deleted consent cookie.",
            "Options").Message);
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A consent cookie template that would delete consent should throw at registration")]
    public async Task AddCookiePolicy_ConsentTemplateWithNonPositiveMaxAge_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            factory.Builder.Services.AddCookiePolicy(policy => policy.Options.ConsentCookie.MaxAge = TimeSpan.Zero));
        Should.Throw<ArgumentException>(() =>
            factory.Builder.Services.AddCookiePolicy(policy => policy.Options.ConsentCookie.MaxAge = TimeSpan.FromSeconds(-1)));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A consent cookie template with a fixed Expires should throw at registration")]
    public async Task AddCookiePolicy_ConsentTemplateWithExpires_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act & Assert
        Should.Throw<ArgumentException>(() => factory.Builder.Services.AddCookiePolicy(policy =>
            policy.Options.ConsentCookie.Expires = DateTimeOffset.UtcNow.AddDays(30)));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: Changing the options after registration should have no effect")]
    public async Task AddCookiePolicy_OptionsChangedAfterRegistration_ShouldKeepTheCapturedPolicy()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        CookiePolicyOptions? captured = null;
        factory.Builder.Services.AddCookiePolicy(policy =>
        {
            policy.Options.HttpOnly = CookieHttpOnlyPolicy.Always;
            captured = policy.Options;
        });
        captured.ShouldNotBeNull().HttpOnly = CookieHttpOnlyPolicy.None;
        factory.Application.UseCookiePolicy();

        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("a", "1")));

        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();

        // Act
        IReadOnlyList<SetCookieField> fields = await CookiePolicyTestHost.SendAsync(client, cancellation.Token);

        // Assert
        fields.ShouldHaveSingleItem().Has("HttpOnly").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: When the policy is registered twice the last registration should win")]
    public async Task UseCookiePolicy_TwoRegistrations_ShouldPlaceTheLast()
    {
        // Arrange — the policies differ in what they add; nothing of the first one may reach the cookie.
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddCookiePolicy(policy => policy.Options.Secure = CookieSecurePolicy.Always);
        factory.Builder.Services.AddCookiePolicy(policy => policy.Options.HttpOnly = CookieHttpOnlyPolicy.Always);
        factory.Application.UseCookiePolicy();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("a", "1")));

        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();

        // Act
        IReadOnlyList<SetCookieField> fields = await CookiePolicyTestHost.SendAsync(client, cancellation.Token);

        // Assert
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Has("HttpOnly").ShouldBeTrue();
        field.Has("Secure").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: A policy AddCookiePolicy builds from the service provider should be the one UseCookiePolicy places")]
    public async Task AddCookiePolicy_WithServiceProviderCallback_ShouldPlaceThePolicy()
    {
        // Arrange — the provider overload builds when the pipeline first resolves the registration.
        await using WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddCookiePolicy((services, policy) => policy.Options.Secure = CookieSecurePolicy.Always);
        factory.Application.UseCookiePolicy();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("a", "1")));

        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();

        // Act
        IReadOnlyList<SetCookieField> fields = await CookiePolicyTestHost.SendAsync(client, cancellation.Token);

        // Assert
        fields.ShouldHaveSingleItem().Has("Secure").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Registration: Without a registration UseCookiePolicy should place the default policy")]
    public async Task UseCookiePolicy_WithoutRegistration_ShouldPlaceTheDefaultPolicy()
    {
        // Arrange
        (bool Installed, bool IsConsentNeeded)? consent = null;
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            ICookieConsentFeature? feature = context.Features.Get<ICookieConsentFeature>();
            consent = (feature is not null, feature?.IsConsentNeeded ?? true);
            context.Response.Cookies.Add(new HttpCookie("long", "1", new HttpCookieOptions { MaxAge = TimeSpan.FromDays(500) }));
        });

        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();

        // Act
        IReadOnlyList<SetCookieField> fields = await CookiePolicyTestHost.SendAsync(client, cancellation.Token);

        // Assert — the default policy is in place: consent installed but not needed, and the 400-day cap.
        consent.ShouldBe((true, false));
        fields.ShouldHaveSingleItem().Get("Max-Age").ShouldBe("34560000");
    }
}
