using System;
using System.Collections.Generic;
using System.Numerics;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Cookies.Tests;

public class HttpCookieValueObjectTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Equals: Default value objects equal themselves")]
    public void Equals_DefaultValueObjects_ShouldBeEqual()
    {
        // Arrange / Act / Assert
        AssertDefaultEquality<HttpCookieName>();
        AssertDefaultEquality<HttpCookieValue>();
        AssertDefaultEquality<HttpCookiePath>();
        AssertDefaultEquality<HttpCookieDomain>();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - GetHashCode: Default value objects work in a hash set")]
    public void GetHashCode_DefaultValueObjects_ShouldSupportHashSets()
    {
        // Arrange / Act / Assert
        AssertDefaultHashing<HttpCookieName>();
        AssertDefaultHashing<HttpCookieValue>();
        AssertDefaultHashing<HttpCookiePath>();
        AssertDefaultHashing<HttpCookieDomain>();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - CompareTo: Default value objects sort before constructed values")]
    public void CompareTo_DefaultValueObjects_ShouldSortFirst()
    {
        // Arrange
        HttpCookieName name = "session";
        HttpCookieValue value = "";
        HttpCookiePath path = "";
        HttpCookieDomain domain = "example.com";

        // Act / Assert
        AssertDefaultOrdering(name);
        AssertDefaultOrdering(value);
        AssertDefaultOrdering(path);
        AssertDefaultOrdering(domain);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - ToString: Default value objects return an empty string")]
    public void ToString_DefaultValueObjects_ShouldReturnEmptyString()
    {
        // Arrange
        HttpCookieName name = default;
        HttpCookieValue value = default;
        HttpCookiePath path = default;
        HttpCookieDomain domain = default;

        // Act
        string[] actual = [name.ToString(), value.ToString(), path.ToString(), domain.ToString()];

        // Assert
        actual.ShouldAllBe(item => item == string.Empty);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Equals: Domains ignore letter casing")]
    public void Equals_DomainsDifferingOnlyByCase_ShouldBeEqual()
    {
        // Arrange
        HttpCookieDomain lower = "example.com";
        HttpCookieDomain upper = "EXAMPLE.COM";

        // Act
        bool equal = lower == upper;
        bool unequal = lower != upper;
        int comparison = lower.CompareTo(upper);

        // Assert
        equal.ShouldBeTrue();
        unequal.ShouldBeFalse();
        lower.Equals(upper).ShouldBeTrue();
        lower.Equals((object)upper).ShouldBeTrue();
        comparison.ShouldBe(0);
        upper.Value.ShouldBe("EXAMPLE.COM");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - GetHashCode: Domains differing only by case share a hash")]
    public void GetHashCode_DomainsDifferingOnlyByCase_ShouldShareHashCode()
    {
        // Arrange
        HttpCookieDomain lower = "example.com";
        HttpCookieDomain upper = "EXAMPLE.COM";

        // Act
        HashSet<HttpCookieDomain> domains = [lower, upper];

        // Assert
        lower.GetHashCode().ShouldBe(upper.GetHashCode());
        domains.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - Equals: Names, values, and paths remain case sensitive")]
    public void Equals_OtherCookieValuesDifferingOnlyByCase_ShouldBeUnequal()
    {
        // Arrange
        HttpCookieName name = "session";
        HttpCookieValue value = "token";
        HttpCookiePath path = "/app";

        // Act
        bool sameName = name == (HttpCookieName)"SESSION";
        bool sameValue = value == (HttpCookieValue)"TOKEN";
        bool samePath = path == (HttpCookiePath)"/APP";

        // Assert
        sameName.ShouldBeFalse();
        sameValue.ShouldBeFalse();
        samePath.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - IsValid: Valid candidates can be checked without an instance")]
    public void IsValid_ValidCandidates_ShouldReturnTrueWithoutInstance()
    {
        // Arrange
        const string name = "session";
        const string value = "token";
        const string path = "/app";
        const string domain = "example.com";

        // Act
        bool validName = HttpCookieName.IsValid(name, out string? nameMessage);
        bool validValue = HttpCookieValue.IsValid(value, out string? valueMessage);
        bool validPath = HttpCookiePath.IsValid(path, out string? pathMessage);
        bool validDomain = HttpCookieDomain.IsValid(domain, out string? domainMessage);

        // Assert
        validName.ShouldBeTrue();
        validValue.ShouldBeTrue();
        validPath.ShouldBeTrue();
        validDomain.ShouldBeTrue();
        nameMessage.ShouldBeNull();
        valueMessage.ShouldBeNull();
        pathMessage.ShouldBeNull();
        domainMessage.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Cookies] - IsValid: Invalid candidates return a reason without an instance")]
    public void IsValid_InvalidCandidates_ShouldReturnFalseWithReason()
    {
        // Arrange
        const string invalid = "bad;value";

        // Act
        bool validName = HttpCookieName.IsValid(invalid, out string? nameMessage);
        bool validValue = HttpCookieValue.IsValid(invalid, out string? valueMessage);
        bool validPath = HttpCookiePath.IsValid(invalid, out string? pathMessage);
        bool validDomain = HttpCookieDomain.IsValid(invalid, out string? domainMessage);

        // Assert
        validName.ShouldBeFalse();
        validValue.ShouldBeFalse();
        validPath.ShouldBeFalse();
        validDomain.ShouldBeFalse();
        nameMessage.ShouldNotBeNullOrEmpty();
        valueMessage.ShouldNotBeNullOrEmpty();
        pathMessage.ShouldNotBeNullOrEmpty();
        domainMessage.ShouldNotBeNullOrEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - TryCreate: Names return a result only for valid input")]
    [InlineData("session", true)]
    [InlineData("bad;name", false)]
    [InlineData(null, false)]
    public void TryCreate_NameCandidate_ShouldReturnExpectedResult(string? candidate, bool expected)
    {
        // Arrange / Act
        bool created = HttpCookieName.TryCreate(candidate, out HttpCookieName result);

        // Assert
        created.ShouldBe(expected);
        result.Value.ShouldBe(expected ? candidate : null);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - TryCreate: Values return a result only for valid input")]
    [InlineData("token", true)]
    [InlineData("", true)]
    [InlineData("bad;value", false)]
    [InlineData(null, false)]
    public void TryCreate_ValueCandidate_ShouldReturnExpectedResult(string? candidate, bool expected)
    {
        // Arrange / Act
        bool created = HttpCookieValue.TryCreate(candidate, out HttpCookieValue result);

        // Assert
        created.ShouldBe(expected);
        result.Value.ShouldBe(expected ? candidate : null);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - TryCreate: Paths return a result only for valid input")]
    [InlineData("/app", true)]
    [InlineData("", true)]
    [InlineData("/app; Secure", false)]
    [InlineData(null, false)]
    public void TryCreate_PathCandidate_ShouldReturnExpectedResult(string? candidate, bool expected)
    {
        // Arrange / Act
        bool created = HttpCookiePath.TryCreate(candidate, out HttpCookiePath result);

        // Assert
        created.ShouldBe(expected);
        result.Value.ShouldBe(expected ? candidate : null);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Cookies] - TryCreate: Domains return a result only for valid input")]
    [InlineData("example.com", true)]
    [InlineData("bad_domain", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryCreate_DomainCandidate_ShouldReturnExpectedResult(string? candidate, bool expected)
    {
        // Arrange / Act
        bool created = HttpCookieDomain.TryCreate(candidate, out HttpCookieDomain result);

        // Assert
        created.ShouldBe(expected);
        result.Value.ShouldBe(expected ? candidate : null);
    }

    private static void AssertDefaultEquality<T>()
        where T : struct, IEquatable<T>, IEqualityOperators<T, T, bool>
    {
        T first = default;
        T second = default;

        bool equal = first == second;
        bool unequal = first != second;

        equal.ShouldBeTrue();
        unequal.ShouldBeFalse();
        first.Equals(second).ShouldBeTrue();
        first.Equals((object)second).ShouldBeTrue();
        first.Equals(null).ShouldBeFalse();
    }

    private static void AssertDefaultHashing<T>() where T : struct
    {
        T value = default;

        HashSet<T> values = [value, default];

        value.GetHashCode().ShouldBe(0);
        values.Count.ShouldBe(1);
        values.Contains(default).ShouldBeTrue();
    }

    private static void AssertDefaultOrdering<T>(T value)
        where T : struct, IComparable<T>, IComparisonOperators<T, T, bool>
    {
        T empty = default;
        T anotherEmpty = default;

        bool less = empty < value;
        bool greater = value > empty;

        less.ShouldBeTrue();
        greater.ShouldBeTrue();
        (empty <= anotherEmpty).ShouldBeTrue();
        (empty >= anotherEmpty).ShouldBeTrue();
        empty.CompareTo(anotherEmpty).ShouldBe(0);
        empty.CompareTo(value).ShouldBeLessThan(0);
        value.CompareTo(empty).ShouldBeGreaterThan(0);
    }
}
