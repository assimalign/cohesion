using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpHostMatcherTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: Create should reject a null pattern collection")]
    public void Create_NullPatterns_ShouldThrowArgumentNull()
    {
        // Arrange
        IEnumerable<string> patterns = null!;

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => HttpHostMatcher.Create(patterns));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: Create should reject an empty allowlist rather than compile a deny-all")]
    public void Create_EmptyPatterns_ShouldThrowArgument()
    {
        // Arrange
        string[] patterns = Array.Empty<string>();

        // Act & Assert
        Should.Throw<ArgumentException>(() => HttpHostMatcher.Create(patterns));
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: Create should fail fast on an invalid pattern")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com:8080")]
    [InlineData("*:8080")]
    [InlineData("[::1]:443")]
    [InlineData("*.")]
    [InlineData("a.*.com")]
    [InlineData("ex*mple.com")]
    [InlineData("*.exa*ple.com")]
    [InlineData("example.com:")]
    [InlineData("example.com:0")]
    [InlineData("[::1")]
    public void Create_InvalidPattern_ShouldThrowArgument(string? pattern)
    {
        // Arrange
        string[] patterns = { pattern! };

        // Act & Assert
        Should.Throw<ArgumentException>(() => HttpHostMatcher.Create(patterns));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: Create should still validate patterns beside a match-any wildcard")]
    public void Create_MatchAnyBesideInvalidPattern_ShouldThrowArgument()
    {
        // Arrange
        string[] patterns = { "*", "example.com:8080" };

        // Act & Assert — the wildcard would dominate, but the typo beside it must still fail
        // loudly at creation rather than being masked.
        Should.Throw<ArgumentException>(() => HttpHostMatcher.Create(patterns));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: Create should compile the * pattern to the match-any matcher")]
    public void Create_MatchAnyPattern_ShouldReturnMatchAnyMatcher()
    {
        // Arrange & Act
        HttpHostMatcher alone = HttpHostMatcher.Create(new[] { "*" });
        HttpHostMatcher beside = HttpHostMatcher.Create(new[] { "*", "example.com" });

        // Assert
        alone.IsMatchAny.ShouldBeTrue();
        beside.IsMatchAny.ShouldBeTrue();
        alone.IsMatch(new HttpHost("anything.example")).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: MatchAny should accept every host, including empty and malformed values")]
    public void MatchAny_AnyHost_ShouldMatch()
    {
        // Arrange
        HttpHostMatcher matcher = HttpHostMatcher.MatchAny;

        // Act & Assert — match-any means "do not filter hosts": structural validation and the
        // empty-host policy are the caller's concern, not the matcher's.
        matcher.IsMatchAny.ShouldBeTrue();
        matcher.IsMatch(new HttpHost("anything.example")).ShouldBeTrue();
        matcher.IsMatch(new HttpHost("example.com:")).ShouldBeTrue();
        matcher.IsMatch(HttpHost.Empty).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: An exact pattern should match case-insensitively and ignore the request port")]
    [InlineData("example.com")]
    [InlineData("EXAMPLE.COM")]
    [InlineData("Example.Com")]
    [InlineData("example.com:8080")]
    [InlineData("EXAMPLE.COM:443")]
    public void IsMatch_ExactPattern_ShouldMatchHostComponentCaseInsensitively(string requestHost)
    {
        // Arrange
        HttpHostMatcher matcher = HttpHostMatcher.Create(new[] { "example.com" });

        // Act & Assert
        matcher.IsMatch(new HttpHost(requestHost)).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: An exact pattern should reject every other host")]
    [InlineData("sub.example.com")]
    [InlineData("example.org")]
    [InlineData("notexample.com")]
    [InlineData("example.com.evil.com")]
    public void IsMatch_ExactPattern_ShouldRejectOtherHosts(string requestHost)
    {
        // Arrange
        HttpHostMatcher matcher = HttpHostMatcher.Create(new[] { "example.com" });

        // Act & Assert
        matcher.IsMatch(new HttpHost(requestHost)).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: A wildcard pattern should match subdomains of any depth")]
    [InlineData("api.example.com")]
    [InlineData("a.b.example.com")]
    [InlineData("API.EXAMPLE.COM")]
    [InlineData("api.example.com:8080")]
    public void IsMatch_WildcardPattern_ShouldMatchSubdomainsOfAnyDepth(string requestHost)
    {
        // Arrange
        HttpHostMatcher matcher = HttpHostMatcher.Create(new[] { "*.example.com" });

        // Act & Assert
        matcher.IsMatch(new HttpHost(requestHost)).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: A wildcard pattern should exclude the apex and label-boundary lookalikes")]
    [InlineData("example.com")]
    [InlineData("evilexample.com")]
    [InlineData("example.com.evil")]
    [InlineData("")]
    public void IsMatch_WildcardPattern_ShouldExcludeApexAndLookalikes(string requestHost)
    {
        // Arrange
        HttpHostMatcher matcher = HttpHostMatcher.Create(new[] { "*.example.com" });

        // Act & Assert
        matcher.IsMatch(new HttpHost(requestHost)).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: IPv6 patterns and hosts should compare bracket-insensitively")]
    [InlineData("[::1]", "[::1]")]
    [InlineData("[::1]", "::1")]
    [InlineData("[::1]", "[::1]:8080")]
    [InlineData("::1", "[::1]")]
    [InlineData("[2001:DB8::1]", "[2001:db8::1]")]
    public void IsMatch_Ipv6Literal_ShouldCompareBracketInsensitively(string pattern, string requestHost)
    {
        // Arrange
        HttpHostMatcher matcher = HttpHostMatcher.Create(new[] { pattern });

        // Act & Assert
        matcher.IsMatch(new HttpHost(requestHost)).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: A malformed or empty request host should never match an allowlist")]
    [InlineData("example.com:")]
    [InlineData("example.com:abc")]
    [InlineData("[::1")]
    [InlineData("")]
    public void IsMatch_MalformedOrEmptyHost_ShouldNotMatch(string requestHost)
    {
        // Arrange
        HttpHostMatcher matcher = HttpHostMatcher.Create(new[] { "example.com", "*.example.com" });

        // Act & Assert
        matcher.IsMatch(new HttpHost(requestHost)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: A mixed allowlist should match across its exact, wildcard, and IPv6 entries")]
    public void IsMatch_MixedPatterns_ShouldMatchAcrossEntries()
    {
        // Arrange
        HttpHostMatcher matcher = HttpHostMatcher.Create(new[] { "api.example.com", "*.internal.example", "[::1]" });

        // Act & Assert
        matcher.IsMatchAny.ShouldBeFalse();
        matcher.IsMatch(new HttpHost("api.example.com")).ShouldBeTrue();
        matcher.IsMatch(new HttpHost("a.internal.example:5000")).ShouldBeTrue();
        matcher.IsMatch(new HttpHost("[::1]:8080")).ShouldBeTrue();
        matcher.IsMatch(new HttpHost("internal.example")).ShouldBeFalse();
        matcher.IsMatch(new HttpHost("example.com")).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHostMatcher: Patterns should be trimmed before compiling")]
    public void Create_PatternWithSurroundingWhitespace_ShouldTrim()
    {
        // Arrange
        HttpHostMatcher matcher = HttpHostMatcher.Create(new[] { "  example.com  " });

        // Act & Assert
        matcher.IsMatch(new HttpHost("example.com")).ShouldBeTrue();
    }
}
