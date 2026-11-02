using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Sessions.Internal;

namespace Assimalign.Cohesion.Web.Sessions.Tests;

public class SessionCookieNameTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - AddSessions: An invalid CookieName fails in the configuration assignment")]
    [MemberData(nameof(InvalidCookieNames))]
    public void AddSessions_CookieNameOutsideGrammar_ShouldThrowAtAssignment(string? cookieName, bool explicitStore)
    {
        // Arrange
        IWebApplicationMiddleware? middleware = null;
        Action<HttpSessionOptions> configure = options => options.CookieName = cookieName!;

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => middleware = BuildSessions(configure, explicitStore));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.InnerException.ShouldBeNull();
        if (!string.IsNullOrEmpty(cookieName))
        {
            exception.ToString().ShouldNotContain(cookieName, Case.Sensitive);
        }

        middleware.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - AddSessions: A token CookieName builds the middleware with or without a supplied store")]
    [InlineData("sid", false)]
    [InlineData("sid", true)]
    [InlineData(".Cohesion.Session", false)]
    [InlineData(".Cohesion.Session", true)]
    [InlineData("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", false)]
    [InlineData("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz", true)]
    public void AddSessions_CookieNameInGrammar_ShouldBuildMiddleware(string cookieName, bool explicitStore)
    {
        // Arrange
        Action<HttpSessionOptions> configure = options => options.CookieName = cookieName;

        // Act
        IWebApplicationMiddleware middleware = BuildSessions(configure, explicitStore);

        // Assert
        middleware.ShouldBeOfType<SessionMiddleware>();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - AddSessions: A default CookieName fails before the middleware is built")]
    [InlineData(false)]
    [InlineData(true)]
    public void AddSessions_DefaultCookieName_ShouldThrowAtRegistration(bool explicitStore)
    {
        // Arrange
        IWebApplicationMiddleware? middleware = null;
        Action<HttpSessionOptions> configure = options => options.CookieName = default;

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => middleware = BuildSessions(configure, explicitStore));

        // Assert
        exception.ParamName.ShouldBe("Options");
        exception.Message.ShouldBe(new ArgumentException(
            "HttpSessionOptions.CookieName must be a non-empty RFC 6265 token with no control characters, whitespace, or separators.",
            "Options").Message);
        exception.InnerException.ShouldBeNull();
        middleware.ShouldBeNull();
    }

    public static TheoryData<string?, bool> InvalidCookieNames
    {
        get
        {
            TheoryData<string?, bool> data = new();
            foreach (string? name in new string?[]
            {
                null, "", "a b", "a\tb", "a=b", "a;b", "a,b", "a/b", "a\\b",
                "a\"b", "a\r\nb", "a\0b", "a\u007fb", "café",
            })
            {
                data.Add(name, false);
                data.Add(name, true);
            }

            return data;
        }
    }

    private static IWebApplicationMiddleware BuildSessions(Action<HttpSessionOptions> configure, bool explicitStore)
    {
        SessionsBuilder sessions = new();
        if (explicitStore)
        {
            sessions.UseStore(new InMemoryHttpSessionStore());
        }

        configure(sessions.Options);
        return sessions.Build();
    }
}
