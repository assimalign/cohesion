using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Authentication.Cookie.Tests;

public sealed class ConfiguredFieldRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Redirect paths: reject every control during configuration")]
    [InlineData(false)]
    [InlineData(true)]
    public void RedirectPath_ControlCharacters_ShouldRejectDuringConfiguration(bool accessDenied)
    {
        // Arrange
        CookieAuthenticationOptions options = new();

        // Act and assert
        for (int code = 0; code <= char.MaxValue; code++)
        {
            if (char.IsControl((char)code))
            {
                string path = $"/account{(char)code}login";
                Should.Throw<ArgumentException>(() =>
                {
                    if (accessDenied)
                    {
                        options.AccessDeniedPath = path;
                    }
                    else
                    {
                        options.LoginPath = path;
                    }
                });
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Redirect paths: reject empty and space-containing locations")]
    [InlineData("")]
    [InlineData("/account login")]
    [InlineData(" /account/login")]
    [InlineData("/account/login ")]
    public void RedirectPath_EmptyOrSpace_ShouldRejectDuringConfiguration(string path)
    {
        // Arrange
        CookieAuthenticationOptions options = new();

        // Act and assert
        Should.Throw<ArgumentException>(() => options.LoginPath = path);
        Should.Throw<ArgumentException>(() => options.AccessDeniedPath = path);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Cookie] - Redirect paths: refuse default before handler creation")]
    public void RedirectPath_Default_ShouldRejectDuringConfiguration()
    {
        // Arrange
        CookieAuthenticationOptions options = new();

        // Act and assert
        Should.Throw<ArgumentException>(() => options.LoginPath = default!);
        Should.Throw<ArgumentException>(() => options.AccessDeniedPath = default!);
    }
}
