using System;
using System.Collections.Generic;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpDefaultValueTests
{
    [Fact(DisplayName = "Cohesion Test [Http] - HttpHeaderKey: Default should support value operations and hash collections")]
    public void GetHashCode_DefaultHttpHeaderKey_ShouldSupportValueOperationsAndHashCollections()
    {
        // Arrange
        HttpHeaderKey value = default;
        HttpHeaderKey other = new("X-Test");
        HttpHeaderKey same = new("x-test");

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();
        string converted = value;

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, string.Empty);
        hashCode.ShouldBe(0);
        converted.ShouldBe(string.Empty);
        value.CompareTo(default).ShouldBe(0);
        value.CompareTo(other).ShouldBeLessThan(0);
        other.CompareTo(value).ShouldBeGreaterThan(0);
        other.CompareTo(same).ShouldBe(0);
        (value == default).ShouldBeTrue();
        (value != other).ShouldBeTrue();
        AssertEquivalentValues(other, same);
        other.ToString().ShouldBe("X-Test");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpMethod: Default should support value operations and hash collections")]
    public void GetHashCode_DefaultHttpMethod_ShouldSupportValueOperationsAndHashCollections()
    {
        // Arrange
        HttpMethod value = default;
        HttpMethod other = HttpMethod.Get;

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();
        string converted = value;

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, string.Empty);
        hashCode.ShouldBe(0);
        converted.ShouldBe(string.Empty);
        (value == default).ShouldBeTrue();
        (value != other).ShouldBeTrue();
        value.IsSafe.ShouldBeFalse();
        value.IsIdempotent.ShouldBeFalse();
        value.IsCacheable.ShouldBeFalse();
        value.CacheKeyIncludesContent.ShouldBeFalse();
        AssertEquivalentValues(other, new HttpMethod("GET"));
        other.Equals(new HttpMethod("get")).ShouldBeFalse();
        other.ToString().ShouldBe("GET");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpPath: Default should support value operations and hash collections")]
    public void GetHashCode_DefaultHttpPath_ShouldSupportValueOperationsAndHashCollections()
    {
        // Arrange
        HttpPath value = default;
        HttpPath other = new("/api");

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();
        string converted = value;
        HttpPath appendedToDefault = value.Concat(other);
        HttpPath defaultAppended = other.Concat(value);
        HttpPath defaultsCombined = value.Concat(default);

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, string.Empty);
        hashCode.ShouldBe(0);
        converted.ShouldBe(string.Empty);
        appendedToDefault.ShouldBe(other);
        defaultAppended.ShouldBe(other);
        defaultsCombined.ShouldBe(default(HttpPath));
        value.Concat(HttpPath.Root).ShouldBe(HttpPath.Root);
        HttpPath.Root.Concat(value).ShouldBe(HttpPath.Root);
        value.StartsWith(default).ShouldBeTrue();
        other.StartsWith(value).ShouldBeTrue();
        value.StartsWith(other).ShouldBeFalse();
        value.ToUriComponent().ShouldBe(string.Empty);
        value.Equals(HttpPath.Root).ShouldBeFalse();
        AssertEquivalentValues(other, new HttpPath("/api"));
        other.Equals(new HttpPath("/API")).ShouldBeFalse();
        other.Concat(new HttpPath("/items")).ToString().ShouldBe("/api/items");
        other.StartsWith(new HttpPath("/a")).ShouldBeTrue();
        other.ToUriComponent().ShouldBeSameAs(other.Value);
        new HttpPath(string.Empty).ShouldBe(HttpPath.Root);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpHost: Default should support value operations and hash collections")]
    public void GetHashCode_DefaultHttpHost_ShouldSupportValueOperationsAndHashCollections()
    {
        // Arrange
        HttpHost value = default;
        HttpHost other = new("Example.COM:443");

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();
        string converted = value;
        bool hasComponents = value.TryGetComponents(out ReadOnlySpan<char> host, out int? port);

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, string.Empty);
        hashCode.ShouldBe(0);
        converted.ShouldBe(string.Empty);
        value.Equals(HttpHost.Empty).ShouldBeFalse();
        HttpHost.Empty.Equals(value).ShouldBeFalse();
        value.IsEmpty.ShouldBeTrue();
        value.Host.ShouldBe(string.Empty);
        value.Port.ShouldBeNull();
        hasComponents.ShouldBeTrue();
        host.IsEmpty.ShouldBeTrue();
        port.ShouldBeNull();
        AssertEquivalentValues(other, new HttpHost("example.com:443"));
        other.ToString().ShouldBe("Example.COM:443");
        other.Host.ShouldBe("Example.COM");
        other.Port.ShouldBe(443);
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpQueryKey: Default should support value operations and hash collections")]
    public void GetHashCode_DefaultHttpQueryKey_ShouldSupportValueOperationsAndHashCollections()
    {
        // Arrange
        HttpQueryKey value = default;
        HttpQueryKey other = new("Page");
        HttpQueryKey same = new("page");

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();
        string converted = value;

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, string.Empty);
        hashCode.ShouldBe(0);
        converted.ShouldBe(string.Empty);
        value.CompareTo(default).ShouldBe(0);
        value.CompareTo(other).ShouldBeLessThan(0);
        other.CompareTo(value).ShouldBeGreaterThan(0);
        other.CompareTo(same).ShouldBe(0);
        (value == default).ShouldBeTrue();
        (value != other).ShouldBeTrue();
        AssertEquivalentValues(other, same);
        other.ToString().ShouldBe("Page");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpQuery: Default should support value operations and hash collections")]
    public void GetHashCode_DefaultHttpQuery_ShouldSupportValueOperationsAndHashCollections()
    {
        // Arrange
        HttpQuery value = default;
        HttpQuery other = new("?Page=1");
        HttpQuery empty = new(string.Empty);

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, string.Empty);
        hashCode.ShouldBe(0);
        value.Equals(empty).ShouldBeFalse();
        empty.Equals(value).ShouldBeFalse();
        value.Parse().Count.ShouldBe(0);
        AssertEquivalentValues(other, new HttpQuery("Page=1"));
        other.Equals(new HttpQuery("page=1")).ShouldBeFalse();
        other.ToString().ShouldBe("Page=1");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpMediaTypeParameter: Default should support value operations and hash collections")]
    public void GetHashCode_DefaultHttpMediaTypeParameter_ShouldSupportValueOperationsAndHashCollections()
    {
        // Arrange
        HttpMediaTypeParameter value = default;
        HttpMediaTypeParameter other = new("Charset", "UTF-8");

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, "=");
        hashCode.ShouldBe(HashCode.Combine(0, 0));
        (value == default).ShouldBeTrue();
        (value != other).ShouldBeTrue();
        value.HasName("charset").ShouldBeFalse();
        AssertEquivalentValues(other, new HttpMediaTypeParameter("charset", "utf-8"));
        other.HasName("CHARSET").ShouldBeTrue();
        other.ToString().ShouldBe("Charset=UTF-8");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpQualityValue: Default should support value operations and hash collections")]
    public void GetHashCode_DefaultHttpQualityValue_ShouldSupportValueOperationsAndHashCollections()
    {
        // Arrange
        HttpQualityValue value = default;
        HttpQualityValue other = new("GZIP", HttpQuality.One);

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, "; q=0");
        hashCode.ShouldBe(HashCode.Combine(0, HttpQuality.Zero));
        (value == default).ShouldBeTrue();
        (value != other).ShouldBeTrue();
        value.IsWildcard.ShouldBeFalse();
        value.Matches("gzip").ShouldBeFalse();
        value.Quality.ShouldBe(HttpQuality.Zero);
        AssertEquivalentValues(other, new HttpQualityValue("gzip", HttpQuality.One));
        other.Equals(new HttpQualityValue("gzip", HttpQuality.Zero)).ShouldBeFalse();
        other.Matches("gzip").ShouldBeTrue();
        other.ToString().ShouldBe("GZIP; q=1");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpForwardedParameter: Default should support value operations and hash collections")]
    public void GetHashCode_DefaultHttpForwardedParameter_ShouldSupportValueOperationsAndHashCollections()
    {
        // Arrange
        HttpForwardedParameter value = default;
        HttpForwardedParameter other = new("Proto", "https");

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, "=");
        hashCode.ShouldBe(HashCode.Combine(0, 0));
        (value == default).ShouldBeTrue();
        (value != other).ShouldBeTrue();
        value.HasName("proto").ShouldBeFalse();
        AssertEquivalentValues(other, new HttpForwardedParameter("proto", "https"));
        other.Equals(new HttpForwardedParameter("proto", "HTTPS")).ShouldBeFalse();
        other.HasName("PROTO").ShouldBeTrue();
        other.ToString().ShouldBe("Proto=https");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpCacheControlExtension: Default should support value operations and hash collections")]
    public void GetHashCode_DefaultHttpCacheControlExtension_ShouldSupportValueOperationsAndHashCollections()
    {
        // Arrange
        HttpCacheControlExtension value = default;
        HttpCacheControlExtension other = new("Custom", "Value");

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, string.Empty);
        hashCode.ShouldBe(HashCode.Combine(0, 0));
        AssertEquivalentValues(other, new HttpCacheControlExtension("custom", "Value"));
        other.Equals(new HttpCacheControlExtension("custom", "value")).ShouldBeFalse();
        new HttpCacheControlExtension("custom", null).Equals(new HttpCacheControlExtension("custom", string.Empty)).ShouldBeFalse();
        other.ToString().ShouldBe("Custom=Value");
        new HttpCacheControlExtension("custom", null).ToString().ShouldBe("custom");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpMediaType: Default should support value operations and hash collections")]
    public void GetHashCode_DefaultHttpMediaType_ShouldSupportValueOperationsAndHashCollections()
    {
        // Arrange
        HttpMediaType value = default;
        HttpMediaType other = new("Application/Vnd.Api+Json; charset=UTF-8");

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();
        string suffix = value.Suffix;

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, string.Empty);
        hashCode.ShouldBe(0);
        suffix.ShouldBe(string.Empty);
        (value == default).ShouldBeTrue();
        (value != other).ShouldBeTrue();
        value.IsEmpty.ShouldBeTrue();
        value.Parameters.ShouldBeEmpty();
        value.Charset.ShouldBeNull();
        value.HasWildcard.ShouldBeFalse();
        value.Includes(other).ShouldBeFalse();
        other.Includes(value).ShouldBeFalse();
        AssertEquivalentValues(other, new HttpMediaType("application/vnd.api+json; CHARSET=utf-8"));
        other.Suffix.ShouldBe("json");
        other.ToString().ShouldBe("application/vnd.api+json; charset=UTF-8");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpRequestTarget: Default should support value operations and hash collections")]
    public void GetHashCode_DefaultHttpRequestTarget_ShouldSupportValueOperationsAndHashCollections()
    {
        // Arrange
        HttpRequestTarget value = default;
        HttpRequestTarget other = HttpRequestTarget.Parse("/api?Page=1", HttpMethod.Get);

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, string.Empty);
        (value == default).ShouldBeTrue();
        (value != other).ShouldBeTrue();
        value.Host.ShouldBe(default(HttpHost));
        value.Path.ShouldBe(default(HttpPath));
        value.Query.ShouldBe(default(HttpQuery));
        AssertEquivalentValues(other, HttpRequestTarget.Parse("/api?Page=1", HttpMethod.Get));
        other.Equals(HttpRequestTarget.Parse("/api?page=1", HttpMethod.Get)).ShouldBeFalse();
        other.ToString().ShouldBe("/api?Page=1");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpForwardedElement: Default should share empty value equality and hashing")]
    public void GetHashCode_DefaultHttpForwardedElement_ShouldShareEmptyValueEqualityAndHashing()
    {
        // Arrange
        HttpForwardedElement value = default;
        HttpForwardedElement empty = HttpForwardedElement.Create();
        HttpForwardedElement other = HttpForwardedElement.Parse("proto=https");

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, string.Empty);
        AssertEquivalentEmptyValues(value, empty);
        (value == empty).ShouldBeTrue();
        (empty != value).ShouldBeFalse();
        (value != other).ShouldBeTrue();
        value.Parameters.ShouldBeEmpty();
        other.ToString().ShouldBe("proto=https");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpForwardedElementCollection: Default should share empty value equality and hashing")]
    public void GetHashCode_DefaultHttpForwardedElementCollection_ShouldShareEmptyValueEqualityAndHashing()
    {
        // Arrange
        HttpForwardedElementCollection value = default;
        HttpForwardedElementCollection empty = HttpForwardedElementCollection.Empty;
        HttpForwardedElementCollection other = HttpForwardedElementCollection.Parse("proto=https");

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, string.Empty);
        AssertEquivalentEmptyValues(value, empty);
        (value == empty).ShouldBeTrue();
        (empty != value).ShouldBeFalse();
        (value != other).ShouldBeTrue();
        value.Count.ShouldBe(0);
        value.IsEmpty.ShouldBeTrue();
        value.Nearest.ShouldBeNull();
        value.AsSpan().IsEmpty.ShouldBeTrue();
        value.Reverse().ShouldBe(empty);
        other.ToString().ShouldBe("proto=https");
    }

    [Fact(DisplayName = "Cohesion Test [Http] - HttpForwardedValues: Default should share empty value equality and hashing")]
    public void GetHashCode_DefaultHttpForwardedValues_ShouldShareEmptyValueEqualityAndHashing()
    {
        // Arrange
        HttpForwardedValues value = default;
        HttpForwardedValues empty = HttpForwardedValues.Empty;
        HttpForwardedValues other = HttpForwardedValues.Parse("https");

        // Act
        int hashCode = value.GetHashCode();
        string text = value.ToString();

        // Assert
        AssertDefaultBehavior(value, other, hashCode, text, string.Empty);
        AssertEquivalentEmptyValues(value, empty);
        (value == empty).ShouldBeTrue();
        (empty != value).ShouldBeFalse();
        (value != other).ShouldBeTrue();
        value.Count.ShouldBe(0);
        value.IsEmpty.ShouldBeTrue();
        value.Nearest.ShouldBeNull();
        value.AsSpan().IsEmpty.ShouldBeTrue();
        value.Reverse().ShouldBe(empty);
        other.ToString().ShouldBe("https");
        other.Equals(HttpForwardedValues.Parse("HTTPS")).ShouldBeFalse();
    }

    private static void AssertDefaultBehavior<T>(T value, T other, int hashCode, string text, string expectedText)
        where T : struct, IEquatable<T>
    {
        // Arrange
        T anotherDefault = default;
        Dictionary<T, int> dictionary = new();
        HashSet<T> set = new();

        // Act
        dictionary[value] = 1;
        dictionary[other] = 2;
        set.Add(value);
        set.Add(other);
        bool addedDuplicate = set.Add(anotherDefault);

        // Assert
        hashCode.ShouldBe(anotherDefault.GetHashCode());
        text.ShouldNotBeNull();
        text.ShouldBe(expectedText);
        value.Equals(anotherDefault).ShouldBeTrue();
        value.Equals((object)anotherDefault).ShouldBeTrue();
        value.Equals((object?)null).ShouldBeFalse();
        value.Equals(other).ShouldBeFalse();
        other.Equals(value).ShouldBeFalse();
        dictionary.Count.ShouldBe(2);
        dictionary[anotherDefault].ShouldBe(1);
        dictionary[other].ShouldBe(2);
        dictionary.Remove(anotherDefault).ShouldBeTrue();
        dictionary.ContainsKey(value).ShouldBeFalse();
        set.Count.ShouldBe(2);
        addedDuplicate.ShouldBeFalse();
        set.Contains(anotherDefault).ShouldBeTrue();
        set.Contains(other).ShouldBeTrue();
        set.Remove(anotherDefault).ShouldBeTrue();
        set.Contains(value).ShouldBeFalse();
    }

    private static void AssertEquivalentValues<T>(T left, T right)
        where T : struct, IEquatable<T>
    {
        left.Equals(right).ShouldBeTrue();
        right.Equals(left).ShouldBeTrue();
        left.Equals((object)right).ShouldBeTrue();
        left.GetHashCode().ShouldBe(right.GetHashCode());
    }

    private static void AssertEquivalentEmptyValues<T>(T value, T empty)
        where T : struct, IEquatable<T>
    {
        // Arrange
        Dictionary<T, int> defaultDictionary = new() { [value] = 1 };
        Dictionary<T, int> emptyDictionary = new() { [empty] = 2 };
        HashSet<T> defaultSet = new() { value };
        HashSet<T> emptySet = new() { empty };

        // Act
        bool foundEmpty = defaultDictionary.TryGetValue(empty, out int fromDefault);
        bool foundDefault = emptyDictionary.TryGetValue(value, out int fromEmpty);
        bool addedEmpty = defaultSet.Add(empty);
        bool addedDefault = emptySet.Add(value);

        // Assert
        AssertEquivalentValues(value, empty);
        empty.ToString().ShouldBe(string.Empty);
        foundEmpty.ShouldBeTrue();
        foundDefault.ShouldBeTrue();
        fromDefault.ShouldBe(1);
        fromEmpty.ShouldBe(2);
        addedEmpty.ShouldBeFalse();
        addedDefault.ShouldBeFalse();
        defaultSet.Count.ShouldBe(1);
        emptySet.Count.ShouldBe(1);
        defaultSet.Contains(empty).ShouldBeTrue();
        emptySet.Contains(value).ShouldBeTrue();
    }
}
