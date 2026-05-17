using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpCookieTests
{
    [Fact]
    public void ToString_WithConfiguredOptions_ShouldSerializeAllConfiguredSegments()
    {
        // Arrange
        DateTimeOffset expires = new(2026, 03, 20, 15, 30, 00, TimeSpan.Zero);
        HttpCookieOptions options = new()
        {
            Domain = "example.com",
            Path = "/app",
            Expires = expires,
            MaxAge = TimeSpan.FromMinutes(30),
            Secure = true,
            HttpOnly = true,
            SameSite = HttpCookieSameSiteMode.Strict,
        };
        options.Extensions.Add("Priority=High");

        HttpCookie cookie = new("session", "abc123", options);

        // Act
        string serialized = cookie.ToString();

        // Assert
        serialized.ShouldStartWith("session=abc123");
        serialized.ShouldContain("; Domain=example.com");
        serialized.ShouldContain("; Path=/app");
        serialized.ShouldContain($"; Expires={expires.ToUniversalTime():R}");
        serialized.ShouldContain("; Max-Age=1800");
        serialized.ShouldContain("; Secure");
        serialized.ShouldContain("; HttpOnly");
        serialized.ShouldContain("; SameSite=Strict");
        serialized.ShouldContain("; Priority=High");
    }
}
