using System;
using System.Collections.Generic;
using System.Linq;
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
/// The attribute floors, end to end: <c>Secure</c>, <c>HttpOnly</c>, and the minimum <c>SameSite</c> are
/// added or raised on cookies the application appends, and nothing the application asked for is removed.
/// </summary>
public class CookiePolicyAttributeTests
{
    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Secure: SameAsRequest should leave a plaintext request's cookie without Secure")]
    public async Task UseCookiePolicy_SameAsRequestOverPlaintext_ShouldNotAddSecure()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("a", "1")));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Raw.ShouldBe("a=1; Path=/");
        field.Has("Secure").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Secure: Always should mark every cookie Secure, even over plaintext")]
    public async Task UseCookiePolicy_SecureAlways_ShouldMarkEveryCookieSecure()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options => options.Secure = CookieSecurePolicy.Always);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(new HttpCookie("a", "1"));
            context.Response.Cookies.Add(new HttpCookie("b", "2"));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.Count.ShouldBe(2);
        fields.ShouldAllBe(field => field.Has("Secure"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Secure: None should leave Secure as the application set it")]
    public async Task UseCookiePolicy_SecureNone_ShouldLeaveSecureAsAppended()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options => options.Secure = CookieSecurePolicy.None);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(new HttpCookie("plain", "1"));
            context.Response.Cookies.Add(new HttpCookie("secure", "1", new HttpCookieOptions { Secure = true }));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.Single(field => field.Name == "plain").Has("Secure").ShouldBeFalse();
        fields.Single(field => field.Name == "secure").Has("Secure").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Secure: A cookie the application marked Secure should stay Secure over plaintext")]
    public async Task UseCookiePolicy_SecureCookieOverPlaintext_ShouldNeverDowngrade()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("a", "1", new HttpCookieOptions { Secure = true })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldHaveSingleItem().Has("Secure").ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - HttpOnly: Always should mark every cookie HttpOnly")]
    public async Task UseCookiePolicy_HttpOnlyAlways_ShouldMarkEveryCookieHttpOnly()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options => options.HttpOnly = CookieHttpOnlyPolicy.Always);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(new HttpCookie("a", "1"));
            context.Response.Cookies.Add(new HttpCookie("b", "2", new HttpCookieOptions { HttpOnly = true }));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.Count.ShouldBe(2);
        fields.ShouldAllBe(field => field.Has("HttpOnly"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - HttpOnly: None should leave HttpOnly as the application set it")]
    public async Task UseCookiePolicy_HttpOnlyNone_ShouldLeaveHttpOnlyAsAppended()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy();
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            context.Response.Cookies.Add(new HttpCookie("script", "1"));
            context.Response.Cookies.Add(new HttpCookie("server", "1", new HttpCookieOptions { HttpOnly = true }));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.Single(field => field.Name == "script").Has("HttpOnly").ShouldBeFalse();
        fields.Single(field => field.Name == "server").Has("HttpOnly").ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Web.CookiePolicy] - SameSite: The minimum policy should raise a weaker SameSite and never lower a stronger one")]
    [InlineData(HttpCookieSameSiteMode.Unspecified, HttpCookieSameSiteMode.Lax, "Lax")]
    [InlineData(HttpCookieSameSiteMode.None, HttpCookieSameSiteMode.Lax, "Lax")]
    [InlineData(HttpCookieSameSiteMode.Lax, HttpCookieSameSiteMode.Lax, "Lax")]
    [InlineData(HttpCookieSameSiteMode.Strict, HttpCookieSameSiteMode.Lax, "Strict")]
    [InlineData(HttpCookieSameSiteMode.Unspecified, HttpCookieSameSiteMode.Strict, "Strict")]
    [InlineData(HttpCookieSameSiteMode.Lax, HttpCookieSameSiteMode.Strict, "Strict")]
    [InlineData(HttpCookieSameSiteMode.Unspecified, HttpCookieSameSiteMode.None, "None")]
    [InlineData(HttpCookieSameSiteMode.Strict, HttpCookieSameSiteMode.None, "Strict")]
    [InlineData(HttpCookieSameSiteMode.Lax, HttpCookieSameSiteMode.Unspecified, "Lax")]
    [InlineData(HttpCookieSameSiteMode.Unspecified, HttpCookieSameSiteMode.Unspecified, null)]
    public async Task UseCookiePolicy_MinimumSameSitePolicy_ShouldRaiseButNeverLower(
        HttpCookieSameSiteMode appended,
        HttpCookieSameSiteMode minimum,
        string? expected)
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options => options.MinimumSameSitePolicy = minimum);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
            context.Response.Cookies.Add(new HttpCookie("a", "1", new HttpCookieOptions { SameSite = appended })));

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Get("SameSite").ShouldBe(expected);
        field.Has("SameSite").ShouldBe(expected is not null);
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Rewrite: A rewritten cookie should keep its value and every attribute the policy did not change")]
    public async Task UseCookiePolicy_RewrittenCookie_ShouldKeepUnrelatedAttributes()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options =>
        {
            options.Secure = CookieSecurePolicy.Always;
            options.TimeProvider = new FixedTimeProvider(CookiePolicyTestHost.Now);
        });
        CookiePolicyTestHost.UseCookieWriter(factory.Application, context =>
        {
            HttpCookieOptions options = new()
            {
                Domain = "example.com",
                Path = "/app",
                MaxAge = TimeSpan.FromHours(1),
                HttpOnly = true,
                SameSite = HttpCookieSameSiteMode.Strict,
            };
            options.Extensions.Add("Partitioned");
            context.Response.Cookies.Add(new HttpCookie("a", "v1", options));
        });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        SetCookieField field = fields.ShouldHaveSingleItem();
        field.Raw.ShouldBe("a=v1; Domain=example.com; Path=/app; Max-Age=3600; Secure; HttpOnly; SameSite=Strict; Partitioned");
    }

    [Fact(DisplayName = "Cohesion Test [Web.CookiePolicy] - Pass-through: A response that appends no cookie should carry no Set-Cookie field")]
    public async Task UseCookiePolicy_NoCookieAppended_ShouldEmitNoSetCookie()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        factory.Application.UseCookiePolicy(options => options.Secure = CookieSecurePolicy.Always);
        CookiePolicyTestHost.UseCookieWriter(factory.Application, _ => { });

        // Act
        IReadOnlyList<SetCookieField> fields = await SendAsync(factory);

        // Assert
        fields.ShouldBeEmpty();
    }

    private static async Task<IReadOnlyList<SetCookieField>> SendAsync(WebApplicationTestFactory factory)
    {
        using CancellationTokenSource cancellation = new(CookiePolicyTestHost.Timeout);
        using HttpClient client = factory.CreateClient();
        return await CookiePolicyTestHost.SendAsync(client, cancellation.Token);
    }
}
