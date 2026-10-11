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
        // Arrange
        HttpAntiforgeryOptions options = new() { CookieName = cookieName! };

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() =>
        {
            _ = explicitOptions
                ? HttpAntiforgery.Create(options)
                : HttpAntiforgery.Create(configure => configure.CookieName = cookieName!);
        });

        // Assert
        exception.ParamName.ShouldBe("options");
        exception.Message.ShouldContain("HttpAntiforgeryOptions.CookieName", Case.Sensitive);
        exception.InnerException.ShouldBeAssignableTo<ArgumentException>().ShouldNotBeNull().ParamName.ShouldBe("name");
        if (!string.IsNullOrEmpty(cookieName))
        {
            exception.ToString().ShouldNotContain(cookieName, Case.Sensitive);
        }
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
        cookie.Name.ShouldBe(cookieName);
        cookie.Value.ShouldBe(tokens.CookieToken);
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
