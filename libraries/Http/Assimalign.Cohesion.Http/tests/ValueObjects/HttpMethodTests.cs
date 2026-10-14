using System;
using Assimalign.Cohesion.Http.Internal;
using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpMethodTests
{
    [Theory(DisplayName = "Cohesion Test [Http] - Constructor: Should keep the token's case, because methods are case-sensitive (RFC 9110 §9.1)")]
    [InlineData("get")]
    [InlineData("pOSt")]
    [InlineData("PUT")]
    [InlineData("COnnECT")]
    public void Constructor_MixedCaseInput_ShouldKeepTheTokenAsGiven(string value)
    {
        // Arrange
        HttpMethod method = value;

        // Act
        string actual = method.Value;

        // Assert
        actual.ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [Http] - Equals: Should compare tokens byte for byte (RFC 9110 §9.1)")]
    [InlineData("get", "GET")]
    [InlineData("Post", "POST")]
    [InlineData("head", "HEAD")]
    [InlineData("connect", "CONNECT")]
    [InlineData("patch", "PATCH")]
    public void Equals_SameTokenInAnotherCase_ShouldNotBeEqual(string value, string standard)
    {
        // Arrange
        HttpMethod method = new(value);
        HttpMethod canonical = HttpMethod.GetCanonicalizedValue(standard);

        // Act
        bool equal = method.Equals(canonical);

        // Assert
        equal.ShouldBeFalse();
        (method == canonical).ShouldBeFalse();
        (method != canonical).ShouldBeTrue();
        method.Equals((object)canonical).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http] - Equals: The same token should be equal, with the same hash code")]
    public void Equals_SameToken_ShouldBeEqualWithTheSameHashCode()
    {
        // Arrange
        HttpMethod built = new("PROPFIND");
        HttpMethod parsed = HttpMethod.GetCanonicalizedValue("PROPFIND");
        HttpMethod get = new("GET");

        // Act
        bool equal = built.Equals(parsed);

        // Assert
        equal.ShouldBeTrue();
        built.GetHashCode().ShouldBe(parsed.GetHashCode());
        get.ShouldBe(HttpMethod.Get);
        get.GetHashCode().ShouldBe(HttpMethod.Get.GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [Http] - GetCanonicalizedValue: An exact standard token should return the shared standard instance")]
    public void GetCanonicalizedValue_StandardMethod_ShouldReturnTheStandardInstance()
    {
        // Arrange
        HttpMethod[] standards =
        [
            HttpMethod.Get, HttpMethod.Head, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete,
            HttpMethod.Connect, HttpMethod.Options, HttpMethod.Trace, HttpMethod.Patch, HttpMethod.Query,
        ];

        foreach (HttpMethod standard in standards)
        {
            // A fresh copy of the token, as a transport hands it over.
            string token = new(standard.Value.AsSpan());

            // Act
            HttpMethod actual = HttpMethod.GetCanonicalizedValue(token);

            // Assert — the standard instance, not a new method built from the input.
            actual.ShouldBe(standard);
            actual.Value.ShouldBeSameAs(standard.Value);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http] - GetCanonicalizedValue: A standard method in another case should be an unknown method")]
    [InlineData("get")]
    [InlineData("Get")]
    [InlineData("head")]
    [InlineData("post")]
    [InlineData("connect")]
    [InlineData("options")]
    [InlineData("query")]
    public void GetCanonicalizedValue_StandardMethodInAnotherCase_ShouldReturnUnknownMethod(string token)
    {
        // Act
        HttpMethod actual = HttpMethod.GetCanonicalizedValue(token);

        // Assert
        actual.Value.ShouldBe(token);
        actual.ShouldNotBe(HttpMethod.GetCanonicalizedValue(token.ToUpperInvariant()));
        actual.IsSafe.ShouldBeFalse();
        actual.IsIdempotent.ShouldBeFalse();
        actual.IsCacheable.ShouldBeFalse();
        actual.CacheKeyIncludesContent.ShouldBeFalse();
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

    [Fact(DisplayName = "Cohesion Test [Http] - GetCanonicalizedValue: Should canonicalize 'QUERY' to HttpMethod.Query")]
    public void GetCanonicalizedValue_QueryToken_ShouldReturnCanonicalQuery()
    {
        // Act — mirrors the behavior of the other nine registered methods.
        HttpMethod actual = HttpMethod.GetCanonicalizedValue("QUERY");

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
