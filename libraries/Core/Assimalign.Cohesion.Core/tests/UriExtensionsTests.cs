using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Core.Tests;

public class UriExtensionsTests
{
    private const string DisplayPrefix = "Cohesion Test [Core] - UriExtensions: ";

    [Fact(DisplayName = DisplayPrefix + "CreateEndpoint normalizes components and preserves the canonical bytes")]
    public void CreateEndpoint_WithValidComponents_ShouldReturnCanonicalEndpoint()
    {
        // Arrange & Act
        Uri endpoint = Uri.CreateEndpoint("HTTPS", "Example.COM", 443, "health ready");

        // Assert
        endpoint.Scheme.ShouldBe("https");
        endpoint.Host.ShouldBe("example.com");
        endpoint.Port.ShouldBe(443);
        endpoint.EndpointPath.ShouldBe("/health%20ready");
        endpoint.ToEndpointString().ShouldBe("https://example.com:443/health%20ready");
        endpoint.ToString().ShouldBe("https://example.com/health ready");
        endpoint.AbsoluteUri.ShouldBe("https://example.com/health%20ready");
    }

    [Fact(DisplayName = DisplayPrefix + "ToEndpointString omits the root slash while retaining the explicit default port")]
    public void ToEndpointString_WithRootPath_ShouldOmitTrailingSlash()
    {
        // Arrange
        Uri endpoint = Uri.CreateEndpoint("http", "example.com", 80);

        // Act
        string value = endpoint.ToEndpointString();

        // Assert
        value.ShouldBe("http://example.com:80");
        endpoint.EndpointPath.ShouldBeNull();
        endpoint.AbsoluteUri.ShouldBe("http://example.com/");
    }

    [Fact(DisplayName = DisplayPrefix + "TryParseEndpoint round-trips an absolute endpoint")]
    public void TryParseEndpoint_WithAbsoluteEndpoint_ShouldReturnUri()
    {
        // Arrange
        const string value = "http://127.0.0.1:8080/api/v1";

        // Act
        bool parsed = Uri.TryParseEndpoint(value, out Uri? endpoint);

        // Assert
        parsed.ShouldBeTrue();
        endpoint.ShouldBe(new Uri(value));
        endpoint!.ToEndpointString().ShouldBe(value);
    }

    [Theory(DisplayName = DisplayPrefix + "TryParseEndpoint rejects values outside the endpoint shape")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("http:///path")]
    [InlineData("http://localhost:0")]
    [InlineData("http://localhost:65536")]
    [InlineData("tcp://host")]
    [InlineData("http://user@localhost:8080/path")]
    [InlineData("http://localhost:8080/path?query=value")]
    [InlineData("http://localhost:8080/path#fragment")]
    public void TryParseEndpoint_WithInvalidValue_ShouldReturnFalse(string? value)
    {
        // Arrange & Act
        bool parsed = Uri.TryParseEndpoint(value, out Uri? endpoint);

        // Assert
        parsed.ShouldBeFalse();
        endpoint.ShouldBeNull();
    }

    [Theory(DisplayName = DisplayPrefix + "CreateEndpoint rejects ports outside the transport range")]
    [InlineData(0)]
    [InlineData(65536)]
    public void CreateEndpoint_WithOutOfRangePort_ShouldThrow(int port)
    {
        // Arrange & Act
        Action action = () => _ = Uri.CreateEndpoint("http", "localhost", port);

        // Assert
        Should.Throw<ArgumentOutOfRangeException>(action);
    }

    [Fact(DisplayName = DisplayPrefix + "ThrowIfNotEndpoint preserves the caller argument name for null")]
    public void ThrowIfNotEndpoint_WithNull_ShouldThrowArgumentNullException()
    {
        // Arrange
        Uri? endpoint = null;

        // Act
        Action action = () => Uri.ThrowIfNotEndpoint(endpoint);

        // Assert
        Should.Throw<ArgumentNullException>(action).ParamName.ShouldBe(nameof(endpoint));
    }

    [Fact(DisplayName = DisplayPrefix + "IPv6 text stays bracketed while the socket host is bare")]
    public void CreateEndpoint_WithIpv6Literal_ShouldUseUriAndSocketHostConventions()
    {
        // Arrange & Act
        Uri endpoint = Uri.CreateEndpoint("http", "::1", 8080);

        // Assert
        endpoint.Host.ShouldBe("[::1]");
        endpoint.IdnHost.ShouldBe("::1");
        endpoint.ToEndpointString().ShouldBe("http://[::1]:8080");
    }

    [Fact(DisplayName = DisplayPrefix + "IdnHost provides the ASCII-compatible socket host")]
    public void IdnHost_WithInternationalizedHost_ShouldReturnAsciiCompatibleName()
    {
        // Arrange
        Uri endpoint = Uri.CreateEndpoint("tcp", "münich.example", 5740);

        // Act & Assert
        endpoint.IdnHost.ShouldBe("xn--mnich-kva.example");
    }

    [Fact(DisplayName = DisplayPrefix + "Uri equality folds an explicit default port and root path")]
    public void Equality_WithEquivalentHttpUris_ShouldUseUriSemantics()
    {
        // Arrange
        var explicitAddress = new Uri("http://h:80/");
        var implicitAddress = new Uri("http://h");

        // Act & Assert
        explicitAddress.ShouldBe(implicitAddress);
        Uri.Equals(explicitAddress, implicitAddress).ShouldBeTrue();
    }
}
