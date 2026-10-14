using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Authentication.Bearer.Tests;

public sealed class ConfiguredFieldRegressionTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Realm: rejects every control before a challenge")]
    public void Realm_ControlCharacters_ShouldRejectDuringConfiguration()
    {
        // Arrange
        JwtBearerOptions options = new();

        // Act and assert
        for (int code = 0; code <= char.MaxValue; code++)
        {
            if (char.IsControl((char)code))
            {
                string realm = $"api{(char)code}realm";
                Should.Throw<ArgumentException>(() => options.Realm = realm);
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Web.Authentication.Bearer] - Realm: rejects boundary whitespace during configuration")]
    [InlineData(" api")]
    [InlineData("api ")]
    [InlineData("\tapi")]
    [InlineData("api\t")]
    public void Realm_BoundaryWhitespace_ShouldRejectDuringConfiguration(string realm)
    {
        // Arrange
        JwtBearerOptions options = new();

        // Act and assert
        Should.Throw<ArgumentException>(() => options.Realm = realm);
    }
}
