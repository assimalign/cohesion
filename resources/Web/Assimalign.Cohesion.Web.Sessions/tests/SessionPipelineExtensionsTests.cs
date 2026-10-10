using System;

using Assimalign.Cohesion.Web.Sessions.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Sessions.Tests;

/// <summary>
/// Covers the checks the <c>UseSessions</c> verb makes when it registers the middleware, so a
/// misconfiguration fails the application at composition instead of failing every request.
/// </summary>
public class SessionPipelineExtensionsTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - UseSessions: A CookiePath outside path-value fails at registration, not at session establishment")]
    [InlineData("/tenant;x")] // ';' would add an attribute to the Set-Cookie line
    [InlineData("/café")] // not a US-ASCII CHAR
    [InlineData("/a\r\nb")] // CR LF
    public void UseSessions_CookiePathOutsideGrammar_ShouldThrowAtRegistration(string cookiePath)
    {
        // Arrange
        TestPipelineBuilder builder = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => builder.UseSessions(options => options.CookiePath = cookiePath));

        // Assert
        exception.ParamName.ShouldBe("configure");
        exception.Message.ShouldContain("HttpSessionOptions.CookiePath");
        exception.InnerException.ShouldBeOfType<ArgumentException>().ParamName.ShouldBe("Path");
        builder.LastMiddleware.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Sessions] - UseSessions: A CookiePath in path-value registers the middleware")]
    [InlineData("/")]
    [InlineData("/app/v1")]
    [InlineData("/caf%C3%A9")] // the percent-encoded form of the refused '/café'
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
