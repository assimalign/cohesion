using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpHostTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - HttpHost: Should split a name-only value into a host with no port")]
    public void TryGetComponents_NameOnly_ShouldYieldHostWithoutPort()
    {
        // Arrange
        HttpHost value = new("example.com");

        // Act
        bool parsed = value.TryGetComponents(out ReadOnlySpan<char> host, out int? port);

        // Assert
        parsed.ShouldBeTrue();
        host.ToString().ShouldBe("example.com");
        port.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHost: Should split host:port values into their components")]
    [InlineData("example.com:8080", "example.com", 8080)]
    [InlineData("127.0.0.1:5000", "127.0.0.1", 5000)]
    [InlineData("localhost:1", "localhost", 1)]
    [InlineData("example.com:65535", "example.com", 65535)]
    [InlineData("example.com:08080", "example.com", 8080)]
    public void TryGetComponents_HostWithPort_ShouldYieldHostAndPort(string value, string expectedHost, int expectedPort)
    {
        // Arrange
        HttpHost httpHost = new(value);

        // Act
        bool parsed = httpHost.TryGetComponents(out ReadOnlySpan<char> host, out int? port);

        // Assert
        parsed.ShouldBeTrue();
        host.ToString().ShouldBe(expectedHost);
        port.ShouldBe(expectedPort);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHost: Should expose bracketed IPv6 literals without their brackets")]
    [InlineData("[::1]", "::1", null)]
    [InlineData("[::1]:8080", "::1", 8080)]
    [InlineData("[2001:db8::1]:8443", "2001:db8::1", 8443)]
    public void TryGetComponents_BracketedIpv6_ShouldStripBrackets(string value, string expectedHost, int? expectedPort)
    {
        // Arrange
        HttpHost httpHost = new(value);

        // Act
        bool parsed = httpHost.TryGetComponents(out ReadOnlySpan<char> host, out int? port);

        // Assert
        parsed.ShouldBeTrue();
        host.ToString().ShouldBe(expectedHost);
        port.ShouldBe(expectedPort);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHost: Should treat an unbracketed IPv6 literal as a portless host")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    public void TryGetComponents_UnbracketedIpv6_ShouldTreatWholeValueAsHost(string value)
    {
        // Arrange
        HttpHost httpHost = new(value);

        // Act
        bool parsed = httpHost.TryGetComponents(out ReadOnlySpan<char> host, out int? port);

        // Assert — multiple colons without brackets are tolerated bracket-insensitively as an
        // IPv6 literal, which therefore cannot carry a port component.
        parsed.ShouldBeTrue();
        host.ToString().ShouldBe(value);
        port.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHost: Should parse the empty value as an empty host")]
    public void TryGetComponents_Empty_ShouldYieldEmptyHost()
    {
        // Arrange
        HttpHost httpHost = HttpHost.Empty;

        // Act
        bool parsed = httpHost.TryGetComponents(out ReadOnlySpan<char> host, out int? port);

        // Assert
        parsed.ShouldBeTrue();
        host.IsEmpty.ShouldBeTrue();
        port.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHost: Should trim surrounding whitespace from the components")]
    public void TryGetComponents_SurroundingWhitespace_ShouldTrim()
    {
        // Arrange
        HttpHost httpHost = new("  example.com:80  ");

        // Act
        bool parsed = httpHost.TryGetComponents(out ReadOnlySpan<char> host, out int? port);

        // Assert
        parsed.ShouldBeTrue();
        host.ToString().ShouldBe("example.com");
        port.ShouldBe(80);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHost: Should reject values that are not structurally host[:port]")]
    [InlineData("example.com:")]
    [InlineData("example.com:0")]
    [InlineData("example.com:abc")]
    [InlineData("example.com:70000")]
    [InlineData("example.com:-1")]
    [InlineData("example.com:+80")]
    [InlineData("example.com: 80")]
    [InlineData("[::1")]
    [InlineData("[")]
    [InlineData("[]")]
    [InlineData("[::1]x")]
    [InlineData("[::1]:")]
    [InlineData("[::1]:abc")]
    public void TryGetComponents_Malformed_ShouldReturnFalse(string value)
    {
        // Arrange
        HttpHost httpHost = new(value);

        // Act
        bool parsed = httpHost.TryGetComponents(out _, out int? port);

        // Assert
        parsed.ShouldBeFalse();
        port.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHost: Host should return the normalized component and preserve case")]
    [InlineData("example.com", "example.com")]
    [InlineData("EXAMPLE.com:80", "EXAMPLE.com")]
    [InlineData("[::1]:8080", "::1")]
    [InlineData("::1", "::1")]
    public void Host_WellFormedValue_ShouldReturnHostComponent(string value, string expected)
    {
        // Arrange
        HttpHost httpHost = new(value);

        // Act
        string host = httpHost.Host;

        // Assert
        host.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHost: Host should return the raw value when the value is malformed")]
    public void Host_MalformedValue_ShouldReturnRawValue()
    {
        // Arrange
        HttpHost httpHost = new("example.com:");

        // Act
        string host = httpHost.Host;

        // Assert
        host.ShouldBe("example.com:");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHost: Port should surface the explicit port and nothing else")]
    [InlineData("example.com:8080", 8080)]
    [InlineData("example.com", null)]
    [InlineData("example.com:abc", null)]
    [InlineData("[::1]:443", 443)]
    public void Port_Value_ShouldSurfaceExplicitPortOnly(string value, int? expected)
    {
        // Arrange
        HttpHost httpHost = new(value);

        // Act
        int? port = httpHost.Port;

        // Assert
        port.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHost: IsEmpty should reflect an absent host, including the default instance")]
    public void IsEmpty_EmptyAndDefaultInstances_ShouldBeTrue()
    {
        // Arrange
        HttpHost empty = HttpHost.Empty;
        HttpHost defaulted = default;
        HttpHost populated = new("example.com");

        // Act & Assert
        empty.IsEmpty.ShouldBeTrue();
        defaulted.IsEmpty.ShouldBeTrue();
        defaulted.Host.ShouldBe(string.Empty);
        populated.IsEmpty.ShouldBeFalse();
    }
}
