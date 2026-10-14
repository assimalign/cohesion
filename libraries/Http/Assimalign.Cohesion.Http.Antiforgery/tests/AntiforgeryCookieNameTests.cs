using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Antiforgery.Tests;

public class AntiforgeryCookieNameTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Antiforgery] - Create: An invalid CookieName fails before token issue")]
    [MemberData(nameof(InvalidCookieNames))]
    public void Create_CookieNameOutsideGrammar_ShouldThrowAtComposition(string? cookieName, bool explicitOptions)
    {
        // Act — the HttpCookieName conversion refuses the string when it is assigned, so an explicit
        // options instance cannot even be initialized with it.
        ArgumentException exception = Should.Throw<ArgumentException>(() =>
        {
            _ = explicitOptions
                ? HttpAntiforgery.Create(new HttpAntiforgeryOptions { CookieName = cookieName! })
                : HttpAntiforgery.Create(configure => configure.CookieName = cookieName!);
        });

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldContain("Cookie name", Case.Sensitive);
        if (!string.IsNullOrEmpty(cookieName))
        {
            exception.ToString().ShouldNotContain(cookieName, Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Create: A default CookieName, which its type cannot validate, fails before token issue")]
    public void Create_DefaultCookieName_ShouldThrowAtComposition()
    {
        // Arrange
        HttpAntiforgeryOptions options = new() { CookieName = default };

        // Act
        ArgumentException fromOptions = Should.Throw<ArgumentException>(() => HttpAntiforgery.Create(options));
        ArgumentException fromConfigure = Should.Throw<ArgumentException>(
            () => HttpAntiforgery.Create(configure => configure.CookieName = default));

        // Assert
        fromOptions.ParamName.ShouldBe("options");
        fromOptions.Message.ShouldContain("HttpAntiforgeryOptions.CookieName", Case.Sensitive);
        fromConfigure.ParamName.ShouldBe("options");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Antiforgery] - Create: A token CookieName reaches the issued cookie through either overload")]
    [InlineData("sid", false)]
    [InlineData("sid", true)]
    [InlineData(".Cohesion.Session", false)]
    [InlineData(".Cohesion.Session", true)]
    [InlineData("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", true)]
    public void Create_CookieNameInGrammar_ShouldStoreCookieWithName(string cookieName, bool explicitOptions)
    {
        // Arrange
        HttpAntiforgeryOptions options = new() { CookieName = cookieName };
        TestHttpContext context = new(HttpMethod.Get);

        // Act
        IHttpAntiforgery antiforgery = explicitOptions
            ? HttpAntiforgery.Create(options)
            : HttpAntiforgery.Create(configure => configure.CookieName = cookieName);
        HttpAntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);

        // Assert
        HttpCookie cookie = context.Response.Cookies.ShouldHaveSingleItem();
        cookie.Name.ShouldBe<HttpCookieName>(cookieName);
        cookie.Value.ShouldBe<HttpCookieValue>(tokens.CookieToken);
    }

    public static TheoryData<string?, bool> InvalidCookieNames
    {
        get
        {
            TheoryData<string?, bool> data = new();
            foreach (string? name in new string?[]
            {
                null, "", "a b", "a\tb", "a=b", "a;b", "a,b", "a/b", "a\\b",
                "a\"b", "a\r\nb", "a\0b", "a\u007fb", "caf\u00e9",
            })
            {
                data.Add(name, false);
                data.Add(name, true);
            }

            return data;
        }
    }
}
