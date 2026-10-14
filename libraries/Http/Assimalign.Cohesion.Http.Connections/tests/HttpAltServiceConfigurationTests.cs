using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

public class HttpAltServiceConfigurationTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - AltSvc: Refuse unsafe authority at assignment")]
    [InlineData("bad\r\n:443")]
    [InlineData("bad\0:443")]
    [InlineData("bad\u0001:443")]
    [InlineData("bad\u007f:443")]
    [InlineData("bad\u0085:443")]
    [InlineData("bad\thost:443")]
    [InlineData("bad host:443")]
    public void Authority_UnsafeText_ShouldRejectBeforeBinding(string authority)
    {
        // Arrange
        HttpAltServiceAdvertisementOptions options = new();

        // Act / Assert
        Should.Throw<ArgumentException>(() => options.Authority = authority);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - AltSvc: Keep same-host and explicit authorities")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(":443")]
    [InlineData("alt.example:8443")]
    [InlineData("[::1]:443")]
    public void Authority_ValidText_ShouldRetainValue(string? authority)
    {
        // Arrange / Act
        HttpAltServiceAdvertisementOptions options = new() { Authority = authority };

        // Assert
        options.Authority.ShouldBe(authority);
    }
}
