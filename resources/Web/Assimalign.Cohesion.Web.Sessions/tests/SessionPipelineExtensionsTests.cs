using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Sessions.Tests;

/// <summary>
/// Covers option validation before the session middleware is registered.
/// </summary>
public class SessionPipelineExtensionsTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - UseSessions: An invalid CookiePath fails in the configuration assignment")]
    [InlineData(null)]
    [InlineData("/tenant;x")]
    [InlineData("/caf\u00e9")]
    [InlineData("/a\r\nb")]
    public void UseSessions_CookiePathOutsideGrammar_ShouldThrowAtAssignment(string? cookiePath)
    {
        // Arrange
        TestPipelineBuilder builder = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => builder.UseSessions(options => options.CookiePath = cookiePath!));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.InnerException.ShouldBeNull();
        builder.LastMiddleware.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - UseSessions: A default CookiePath fails before middleware registration")]
    [InlineData(false)]
    [InlineData(true)]
    public void UseSessions_DefaultCookiePath_ShouldThrowAtRegistration(bool explicitStore)
    {
        // Arrange
        TestPipelineBuilder builder = new();
        Action<HttpSessionOptions> configure = options => options.CookiePath = default;

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
            "HttpSessionOptions.CookiePath is not an RFC 6265 path-value: it must be US-ASCII with no ';' and no control characters. Percent-encode any other character.",
            "configure").Message);
        exception.InnerException.ShouldBeNull();
        builder.LastMiddleware.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - UseSessions: A CookiePath in path-value registers the middleware")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/app/v1")]
    [InlineData("/caf%C3%A9")]
    public void UseSessions_CookiePathInGrammar_ShouldRegisterMiddleware(string cookiePath)
    {
        // Arrange
        TestPipelineBuilder builder = new();

        // Act
        builder.UseSessions(options => options.CookiePath = cookiePath);

        // Assert
        builder.LastMiddleware.ShouldNotBeNull();
    }
}
