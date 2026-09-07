using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Core;

namespace Assimalign.Cohesion.Core.Tests;

public class EndpointAddressTests
{
    private const string DisplayPrefix = "Cohesion Test [Core] - EndpointAddress: ";

    [Fact(DisplayName = DisplayPrefix + "Constructor normalizes and exposes an absolute endpoint URI")]
    public void Constructor_WithValidComponents_ShouldExposeNormalizedAddress()
    {
        // Arrange & Act
        var address = new EndpointAddress("HTTPS", "Example.COM", 443, "health ready");

        // Assert
        address.Scheme.ShouldBe("https");
        address.Host.ShouldBe("example.com");
        address.Port.ShouldBe(443);
        address.Path.ShouldBe("/health%20ready");
        address.ToString().ShouldBe("https://example.com:443/health%20ready");
        address.Url.IsAbsoluteUri.ShouldBeTrue();
    }

    [Fact(DisplayName = DisplayPrefix + "Parse preserves the explicit port and path")]
    public void Parse_WithAbsoluteUri_ShouldReturnEndpointAddress()
    {
        // Arrange
        const string value = "http://127.0.0.1:8080/api/v1";

        // Act
        EndpointAddress address = EndpointAddress.Parse(value);

        // Assert
        address.ShouldBe(new EndpointAddress("http", "127.0.0.1", 8080, "/api/v1"));
        address.ToString().ShouldBe(value);
    }

    [Theory(DisplayName = DisplayPrefix + "TryParse rejects values outside the endpoint address shape")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("http://localhost:8080/path?query=value")]
    [InlineData("http://localhost:8080/path#fragment")]
    public void TryParse_WithInvalidValue_ShouldReturnFalse(string? value)
    {
        // Arrange & Act
        bool parsed = EndpointAddress.TryParse(value, out EndpointAddress address);

        // Assert
        parsed.ShouldBeFalse();
        address.ShouldBe(default);
    }

    [Fact(DisplayName = DisplayPrefix + "Record equality compares normalized endpoint values")]
    public void Equality_WithEquivalentAddresses_ShouldUseValueSemantics()
    {
        // Arrange
        var first = new EndpointAddress("HTTPS", "EXAMPLE.COM", 8443, "ready");
        var second = new EndpointAddress("https", "example.com", 8443, "/ready");

        // Act & Assert
        first.ShouldBe(second);
        first.GetHashCode().ShouldBe(second.GetHashCode());
    }

    [Theory(DisplayName = DisplayPrefix + "Constructor rejects ports outside the transport range")]
    [InlineData(0)]
    [InlineData(65536)]
    public void Constructor_WithOutOfRangePort_ShouldThrow(int port)
    {
        // Arrange & Act
        Action action = () => _ = new EndpointAddress("http", "localhost", port);

        // Assert
        Should.Throw<ArgumentOutOfRangeException>(action);
    }
}
