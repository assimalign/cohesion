using System;
using Assimalign.Cohesion.Http.Internal;
using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpMethodTests
{
    [Theory]
    [InlineData("get", "GET")]
    [InlineData("pOSt", "POST")]
    [InlineData("PUT", "PUT")]
    [InlineData("COnnECT", "CONNECT")]
    public void Constructor_MixedCaseInput_ShouldNormalizeToUpperInvariant(string value, string expected)
    {
        // Arrange
        HttpMethod method = value;

        // Act
        string actual = method.Value;

        // Assert
        actual.ShouldBe(expected);
    }

    [Fact]
    public void GetCanonicalizedValue_StandardMethod_ShouldReturnEqualValue()
    {
        // Arrange
        const string method = "get";

        // Act
        HttpMethod actual = HttpMethod.GetCanonicalizedValue(method);

        // Assert
        actual.ShouldBe(HttpMethod.Get);
    }

    [Fact]
    public void Constructor_InvalidCharacter_ShouldThrowHttpException()
    {
        // Arrange
        const string method = "GE T";

        // Act
        Action action = () => _ = new HttpMethod(method);

        // Assert
        action.ShouldThrow<ArgumentException>();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Query: Should expose the canonical QUERY token (RFC 10008)")]
    public void Query_ShouldExposeCanonicalQueryToken()
    {
        // Act
        HttpMethod query = HttpMethod.Query;

        // Assert
        query.Value.ShouldBe("QUERY");
        query.ShouldBe(new HttpMethod("QUERY"));
    }

    [Fact(DisplayName = "Cohesion Test [Http] - GetCanonicalizedValue: Should canonicalize 'query' to HttpMethod.Query")]
    public void GetCanonicalizedValue_QueryToken_ShouldReturnCanonicalQuery()
    {
        // Act — mirrors the behavior of the other nine registered methods.
        HttpMethod actual = HttpMethod.GetCanonicalizedValue("query");

        // Assert
        actual.ShouldBe(HttpMethod.Query);
        actual.Value.ShouldBe("QUERY");
    }

    [Theory(DisplayName = "Cohesion Test [Http] - Classification: Should report RFC 9110 §9.2 safe/idempotent/cacheable per method")]
    // method,     isSafe, isIdempotent, isCacheable, cacheKeyIncludesContent
    [InlineData("GET", true, true, true, false)]
    [InlineData("HEAD", true, true, true, false)]
    [InlineData("OPTIONS", true, true, false, false)]
    [InlineData("TRACE", true, true, false, false)]
    [InlineData("QUERY", true, true, true, true)]
    [InlineData("POST", false, false, true, false)]
    [InlineData("PUT", false, true, false, false)]
    [InlineData("DELETE", false, true, false, false)]
    [InlineData("PATCH", false, false, false, false)]
    [InlineData("CONNECT", false, false, false, false)]
    public void Classification_KnownMethod_ShouldMatchRfc(
        string token,
        bool isSafe,
        bool isIdempotent,
        bool isCacheable,
        bool cacheKeyIncludesContent)
    {
        // Arrange
        HttpMethod method = HttpMethod.GetCanonicalizedValue(token);

        // Assert
        method.IsSafe.ShouldBe(isSafe);
        method.IsIdempotent.ShouldBe(isIdempotent);
        method.IsCacheable.ShouldBe(isCacheable);
        method.CacheKeyIncludesContent.ShouldBe(cacheKeyIncludesContent);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Classification: Should report false for an unknown extension method")]
    public void Classification_UnknownMethod_ShouldReportFalseForEveryProperty()
    {
        // Arrange — an unregistered extension token has unknown semantics.
        HttpMethod method = HttpMethod.GetCanonicalizedValue("FROBNICATE");

        // Assert
        method.IsSafe.ShouldBeFalse();
        method.IsIdempotent.ShouldBeFalse();
        method.IsCacheable.ShouldBeFalse();
        method.CacheKeyIncludesContent.ShouldBeFalse();
    }
}
