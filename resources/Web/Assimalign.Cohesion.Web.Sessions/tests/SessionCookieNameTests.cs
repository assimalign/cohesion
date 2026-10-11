using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Sessions.Tests;

public class SessionCookieNameTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - UseSessions: An invalid CookieName fails before middleware registration")]
    [MemberData(nameof(InvalidCookieNames))]
    public void UseSessions_CookieNameOutsideGrammar_ShouldThrowAtRegistration(string? cookieName, bool explicitStore)
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
        exception.ParamName.ShouldBe("configure");
        exception.Message.ShouldContain("HttpSessionOptions.CookieName", Case.Sensitive);
        exception.InnerException.ShouldBeAssignableTo<ArgumentException>().ShouldNotBeNull().ParamName.ShouldBe("name");
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
