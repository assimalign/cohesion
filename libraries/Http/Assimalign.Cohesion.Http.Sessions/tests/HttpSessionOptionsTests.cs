using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpSessionOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - HttpSessionOptions: Defaults match conventional values")]
    public void HttpSessionOptions_Defaults_ShouldMatchConventionalValues()
    {
        // Arrange / Act
        HttpSessionOptions options = new();

        // Assert
        HttpCookieName retainedName = options.CookieName;
        retainedName.ShouldBe<HttpCookieName>(".Cohesion.Session");
        HttpCookiePath retainedPath = options.CookiePath;
        retainedPath.ShouldBe<HttpCookiePath>("/");
        options.CookieHttpOnly.ShouldBeTrue();
        options.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(20));
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - HttpSessionOptions: Properties remain mutable")]
    public void HttpSessionOptions_CustomValues_ShouldBeMutable()
    {
        // Arrange / Act
        HttpSessionOptions options = new()
        {
            CookieName = "custom",
            CookiePath = "/app",
            CookieHttpOnly = false,
            IdleTimeout = TimeSpan.FromMinutes(5),
        };

        // Assert
        HttpCookieName retainedName = options.CookieName;
        retainedName.ShouldBe<HttpCookieName>("custom");
        HttpCookiePath retainedPath = options.CookiePath;
        retainedPath.ShouldBe<HttpCookiePath>("/app");
        options.CookieHttpOnly.ShouldBeFalse();
        options.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Theory(DisplayName = "Cohesion Test [Http.Sessions] - CookieName: Invalid strings fail at assignment")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a=b")]
    [InlineData("a;b")]
    [InlineData("a\r\nb")]
    [InlineData("a\0b")]
    [InlineData("caf\u00e9")]
    public void CookieName_OutsideGrammar_ShouldThrowAtAssignment(string? cookieName)
    {
        // Arrange
        HttpSessionOptions options = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => options.CookieName = cookieName!);

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.InnerException.ShouldBeNull();
        HttpCookieName retainedName = options.CookieName;
        retainedName.ShouldBe<HttpCookieName>(".Cohesion.Session");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Sessions] - CookiePath: Invalid strings fail at assignment")]
    [InlineData(null)]
    [InlineData("/tenant;x")]
    [InlineData("/a\r\nb")]
    [InlineData("/a\0b")]
    [InlineData("/a\u007fb")]
    [InlineData("/caf\u00e9")]
    public void CookiePath_OutsideGrammar_ShouldThrowAtAssignment(string? cookiePath)
    {
        // Arrange
        HttpSessionOptions options = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => options.CookiePath = cookiePath!);

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.InnerException.ShouldBeNull();
        HttpCookiePath retainedPath = options.CookiePath;
        retainedPath.ShouldBe<HttpCookiePath>("/");
    }
}
