using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Sessions.Tests;

public class SessionCookieNameTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - UseSessions: An invalid CookieName fails in the configuration assignment")]
    [MemberData(nameof(InvalidCookieNames))]
    public void UseSessions_CookieNameOutsideGrammar_ShouldThrowAtAssignment(string? cookieName, bool explicitStore)
    {
        // Arrange
        TestPipelineBuilder builder = new();
        Action<HttpSessionOptions> configure = options => options.CookieName = cookieName!;

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() =>
        {
            if (explicitStore)
            {
                builder.UseSessions(new InMemoryHttpSessionStore(), configure);
            }
            else
            {
                builder.UseSessions(configure);
            }
        });

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.InnerException.ShouldBeNull();
        if (!string.IsNullOrEmpty(cookieName))
        {
            exception.ToString().ShouldNotContain(cookieName, Case.Sensitive);
        }

        builder.LastMiddleware.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - UseSessions: A token CookieName registers middleware through either overload")]
    [InlineData("sid", false)]
    [InlineData("sid", true)]
    [InlineData(".Cohesion.Session", false)]
    [InlineData(".Cohesion.Session", true)]
    [InlineData("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", true)]
    public void UseSessions_CookieNameInGrammar_ShouldRegisterMiddleware(string cookieName, bool explicitStore)
    {
        // Arrange
        TestPipelineBuilder builder = new();
        Action<HttpSessionOptions> configure = options => options.CookieName = cookieName;

        // Act
        IWebApplicationPipelineBuilder returned = explicitStore
            ? builder.UseSessions(new InMemoryHttpSessionStore(), configure)
            : builder.UseSessions(configure);

        // Assert
        returned.ShouldBeSameAs(builder);
        builder.LastMiddleware.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - UseSessions: A default CookieName fails before middleware registration")]
    [InlineData(false)]
    [InlineData(true)]
    public void UseSessions_DefaultCookieName_ShouldThrowAtRegistration(bool explicitStore)
    {
        // Arrange
        TestPipelineBuilder builder = new();
        Action<HttpSessionOptions> configure = options => options.CookieName = default;

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() =>
        {
            if (explicitStore)
            {
                builder.UseSessions(new InMemoryHttpSessionStore(), configure);
            }
            else
            {
                builder.UseSessions(configure);
            }
        });

        // Assert
        exception.ParamName.ShouldBe("configure");
        exception.Message.ShouldBe(new ArgumentException(
            "HttpSessionOptions.CookieName must be a non-empty RFC 6265 token with no control characters, whitespace, or separators.",
            "configure").Message);
        exception.InnerException.ShouldBeNull();
        builder.LastMiddleware.ShouldBeNull();
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
