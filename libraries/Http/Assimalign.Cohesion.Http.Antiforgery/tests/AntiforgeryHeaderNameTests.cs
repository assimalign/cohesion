using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Antiforgery.Tests;

public class AntiforgeryHeaderNameTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Antiforgery] - Create: Invalid header names fail before requests")]
    [InlineData("")]
    [InlineData("X CSRF")]
    [InlineData(":method")]
    [InlineData("X-CSRF\r\nInjected")]
    [InlineData("X-CSRF\0")]
    [InlineData("X-Caf\u00e9")]
    public void Create_InvalidHeaderName_ShouldThrowAtComposition(string name)
    {
        // Arrange
        Action configure = () => HttpAntiforgery.Create(options => options.HeaderName = name);

        // Act and assert
        configure.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - HeaderName: Default cannot bypass validation after composition")]
    public void HeaderName_DefaultAfterComposition_ShouldThrow()
    {
        // Arrange
        HttpAntiforgeryOptions options = new();
        _ = HttpAntiforgery.Create(options);

        // Act and assert
        Should.Throw<ArgumentException>(() => options.HeaderName = default);
    }
}
