using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpAltServiceConfigurationTests
{
    [Theory(DisplayName = "Cohesion Test [Http] - AltSvc: Refuse unsafe host before formatting")]
    [InlineData("bad\r\nhost")]
    [InlineData("bad\0host")]
    [InlineData("bad\u0001host")]
    [InlineData("bad\u007fhost")]
    [InlineData("bad\u0085host")]
    [InlineData("bad\thost")]
    [InlineData("bad host")]
    public void Create_UnsafeHost_ShouldReject(string host)
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentException>(() => new HttpAltService("h3", host, 443));
        Should.Throw<ArgumentException>(() => HttpAltService.Http3(host, 443));
    }

    [Theory(DisplayName = "Cohesion Test [Http] - AltSvc: Invalid parsed host fails without throwing")]
    [InlineData("h3=\"bad\r\n:443\"")]
    [InlineData("h3=\"bad\0:443\"")]
    [InlineData("h3=\"bad host:443\"")]
    [InlineData("h3=\"bad\thost:443\"")]
    public void TryParse_UnsafeHost_ShouldReturnFalse(string field)
    {
        // Act
        bool accepted = HttpAltService.TryParse(field, out _);

        // Assert
        accepted.ShouldBeFalse();
    }
}
