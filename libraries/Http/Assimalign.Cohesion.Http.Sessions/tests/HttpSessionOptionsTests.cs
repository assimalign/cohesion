using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpSessionOptionsTests
{
    [Fact]
    public void Defaults_ShouldMatchConventionalValues()
    {
        HttpSessionOptions options = new();

        options.CookieName.ShouldBe(".Cohesion.Session");
        options.CookiePath.ShouldBe("/");
        options.CookieHttpOnly.ShouldBeTrue();
        options.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Properties_ShouldBeMutable()
    {
        HttpSessionOptions options = new()
        {
            CookieName = "custom",
            CookiePath = "/app",
            CookieHttpOnly = false,
            IdleTimeout = TimeSpan.FromMinutes(5),
        };

        options.CookieName.ShouldBe("custom");
        options.CookiePath.ShouldBe("/app");
        options.CookieHttpOnly.ShouldBeFalse();
        options.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(5));
    }
}
