using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing.Exceptions;
using Assimalign.Cohesion.Web.Routing.Metadata;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.Routing.Tests;

public class RouteHostConstraintTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Routing] - TryParse: Well-formed patterns parse into host and port")]
    [InlineData("api.example.com", "api.example.com", null)]
    [InlineData("api.example.com:8080", "api.example.com", 8080)]
    [InlineData("*.example.com", "*.example.com", null)]
    [InlineData("*.example.com:443", "*.example.com", 443)]
    [InlineData("*", "*", null)]
    [InlineData("*:5000", "*", 5000)]
    [InlineData("localhost", "localhost", null)]
    [InlineData("[::1]", "::1", null)]
    [InlineData("[::1]:8080", "::1", 8080)]
    [InlineData("[2001:db8::1]:443", "2001:db8::1", 443)]
    [InlineData("::1", "::1", null)]
    [InlineData(" api.example.com ", "api.example.com", null)]
    public void TryParse_WellFormedPattern_ShouldParseHostAndPort(string pattern, string expectedHost, int? expectedPort)
    {
        // Act
        bool parsed = RouteHostConstraint.TryParse(pattern, out RouteHostConstraint constraint);

        // Assert
        parsed.ShouldBeTrue();
        constraint.Host.ShouldBe(expectedHost);
        constraint.Port.ShouldBe(expectedPort);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - TryParse: Malformed patterns are rejected")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*.")]
    [InlineData(":8080")]
    [InlineData("example.com:")]
    [InlineData("example.com:0")]
    [InlineData("example.com:65536")]
    [InlineData("example.com:-1")]
    [InlineData("example.com:abc")]
    [InlineData("[::1")]
    [InlineData("[]")]
    [InlineData("[::1]8080")]
    [InlineData("[::1]:")]
    [InlineData("foo.*")]
    [InlineData("a*b.example.com")]
    [InlineData("*.*.example.com")]
    public void TryParse_MalformedPattern_ShouldReturnFalse(string? pattern)
    {
        // Act
        bool parsed = RouteHostConstraint.TryParse(pattern, out _);

        // Assert
        parsed.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Parse: Malformed pattern throws RoutePatternException")]
    public void Parse_MalformedPattern_ShouldThrowRoutePatternException()
    {
        // Act
        RoutePatternException exception = Should.Throw<RoutePatternException>(() => RouteHostConstraint.Parse("*."));

        // Assert
        exception.Pattern.ShouldBe("*.");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Parse: Kind flags reflect the parsed pattern")]
    public void Parse_PatternKinds_ShouldExposeKindFlags()
    {
        // Act
        RouteHostConstraint exact = RouteHostConstraint.Parse("api.example.com");
        RouteHostConstraint wildcard = RouteHostConstraint.Parse("*.example.com");
        RouteHostConstraint any = RouteHostConstraint.Parse("*:5000");

        // Assert
        exact.IsSubdomainWildcard.ShouldBeFalse();
        exact.MatchesAnyHost.ShouldBeFalse();
        wildcard.IsSubdomainWildcard.ShouldBeTrue();
        wildcard.MatchesAnyHost.ShouldBeFalse();
        any.MatchesAnyHost.ShouldBeTrue();
        any.IsSubdomainWildcard.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - IsMatch: Exact hosts compare case-insensitively")]
    [InlineData("api.example.com", "api.example.com", true)]
    [InlineData("api.example.com", "API.EXAMPLE.COM", true)]
    [InlineData("API.example.com", "api.example.com", true)]
    [InlineData("api.example.com", "www.example.com", false)]
    [InlineData("api.example.com", "example.com", false)]
    [InlineData("api.example.com", "", false)]
    public void IsMatch_ExactHost_ShouldCompareCaseInsensitively(string pattern, string requestHost, bool expected)
    {
        // Arrange
        RouteHostConstraint constraint = RouteHostConstraint.Parse(pattern);

        // Act + Assert
        constraint.IsMatch(new HttpHost(requestHost)).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - IsMatch: Wildcard requires a subdomain label")]
    [InlineData("*.example.com", "api.example.com", true)]
    [InlineData("*.example.com", "API.Example.COM", true)]
    [InlineData("*.example.com", "a.b.example.com", true)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("*.example.com", "notexample.com", false)]
    [InlineData("*.example.com", "example.com.evil.io", false)]
    public void IsMatch_WildcardSubdomain_ShouldRequireSubdomainLabel(string pattern, string requestHost, bool expected)
    {
        // Arrange
        RouteHostConstraint constraint = RouteHostConstraint.Parse(pattern);

        // Act + Assert
        constraint.IsMatch(new HttpHost(requestHost)).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - IsMatch: Port constraints require an explicit matching port")]
    [InlineData("api.example.com:8080", "api.example.com:8080", true)]
    [InlineData("api.example.com:8080", "api.example.com:9090", false)]
    [InlineData("api.example.com:8080", "api.example.com", false)]
    [InlineData("api.example.com", "api.example.com:8080", true)]
    [InlineData("*:8080", "anything.example.com:8080", true)]
    [InlineData("*:8080", "anything.example.com", false)]
    [InlineData("*.example.com:443", "api.example.com:443", true)]
    [InlineData("*.example.com:443", "api.example.com", false)]
    public void IsMatch_PortConstraint_ShouldRequireExplicitMatchingPort(string pattern, string requestHost, bool expected)
    {
        // Arrange
        RouteHostConstraint constraint = RouteHostConstraint.Parse(pattern);

        // Act + Assert
        constraint.IsMatch(new HttpHost(requestHost)).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - IsMatch: IPv6 literals match bracket-insensitively")]
    [InlineData("[::1]", "[::1]", true)]
    [InlineData("[::1]", "[::1]:8080", true)]
    [InlineData("[::1]:8080", "[::1]:8080", true)]
    [InlineData("[::1]:8080", "[::1]:9090", false)]
    [InlineData("[::1]:8080", "[::1]", false)]
    [InlineData("::1", "[::1]", true)]
    [InlineData("[2001:DB8::1]", "[2001:db8::1]:443", true)]
    [InlineData("[::1]", "localhost", false)]
    public void IsMatch_IPv6Literal_ShouldMatchBracketInsensitively(string pattern, string requestHost, bool expected)
    {
        // Arrange
        RouteHostConstraint constraint = RouteHostConstraint.Parse(pattern);

        // Act + Assert
        constraint.IsMatch(new HttpHost(requestHost)).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - IsMatch: Any-host wildcard matches every host")]
    public void IsMatch_AnyHost_ShouldMatchEveryHost()
    {
        // Arrange
        RouteHostConstraint constraint = RouteHostConstraint.Parse("*");

        // Act + Assert
        constraint.IsMatch(new HttpHost("example.com")).ShouldBeTrue();
        constraint.IsMatch(new HttpHost("api.example.com:8080")).ShouldBeTrue();
        constraint.IsMatch(new HttpHost("[::1]")).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - IsMatch: Default-initialized constraint matches nothing")]
    public void IsMatch_DefaultInstance_ShouldMatchNothing()
    {
        // Arrange
        RouteHostConstraint constraint = default;

        // Act + Assert
        constraint.IsMatch(new HttpHost("example.com")).ShouldBeFalse();
        constraint.IsMatch(HttpHost.Empty).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Web.Routing] - ToString: Canonical text round-trips through Parse")]
    [InlineData("api.example.com", "api.example.com")]
    [InlineData("*.example.com:443", "*.example.com:443")]
    [InlineData("[::1]:8080", "[::1]:8080")]
    [InlineData("::1", "[::1]")]
    [InlineData("*:5000", "*:5000")]
    public void ToString_ParsedConstraint_ShouldProduceCanonicalRoundTrippableText(string pattern, string expected)
    {
        // Arrange
        RouteHostConstraint constraint = RouteHostConstraint.Parse(pattern);

        // Act
        string text = constraint.ToString();

        // Assert
        text.ShouldBe(expected);
        RouteHostConstraint.Parse(text).ShouldBe(constraint);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Routing] - Equals: Constraints compare host case-insensitively and port exactly")]
    public void Equals_SameHostDifferentCase_ShouldBeEqual()
    {
        // Arrange
        RouteHostConstraint first = RouteHostConstraint.Parse("API.example.com:8080");
        RouteHostConstraint second = RouteHostConstraint.Parse("api.EXAMPLE.com:8080");
        RouteHostConstraint differentPort = RouteHostConstraint.Parse("api.example.com:9090");

        // Act + Assert
        first.Equals(second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
        first.Equals(differentPort).ShouldBeFalse();
    }
}
