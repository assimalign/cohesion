using System;
using System.Text.Json;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationPathTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Parse colon format")]
    public void Path_ParseColon_ShouldSplit()
    {
        Path path = Path.Parse("key1:key2:key3");

        Assert.Equal(3, path.Count);
        Assert.Equal("key1", path[0].ToString());
        Assert.Equal("key2", path[1].ToString());
        Assert.Equal("key3", path[2].ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Parse slash format")]
    public void Path_ParseSlash_ShouldSplit()
    {
        Path path = Path.Parse("/key1/key2/key3");

        Assert.Equal(3, path.Count);
        Assert.Equal("key1", path[0].ToString());
        Assert.Equal("key3", path[2].ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Parse backslash format")]
    public void Path_ParseBackslash_ShouldSplit()
    {
        Path path = Path.Parse("key1\\key2\\key3");

        Assert.Equal(3, path.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Parse dot format")]
    public void Path_ParseDot_ShouldSplit()
    {
        Path path = Path.Parse("key1.key2.key3");

        Assert.Equal(3, path.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Mixed format equality")]
    public void Path_MixedFormat_ShouldBeEqual()
    {
        Path path1 = Path.Parse("/key1.key2\\key3:key4");
        Path path2 = Path.Parse("key1:key2:key3:key4");

        Assert.Equal(path1, path2);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Implicit conversion from string")]
    public void Path_ImplicitFromString_ShouldParse()
    {
        Path path = "key1:key2";

        Assert.Equal(2, path.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Implicit conversion to string")]
    public void Path_ImplicitToString_ShouldFormat()
    {
        Path path = Path.Parse("key1:key2:key3");
        string str = path;

        Assert.Equal("key1:key2:key3", str);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Single key is not composite")]
    public void Path_SingleKey_ShouldNotBeComposite()
    {
        Path path = Path.Parse("key1");

        Assert.False(path.IsComposite);
        Assert.Equal(1, path.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Multiple keys is composite")]
    public void Path_MultipleKeys_ShouldBeComposite()
    {
        Path path = Path.Parse("key1:key2");

        Assert.True(path.IsComposite);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Empty string returns empty path")]
    public void Path_EmptyString_ShouldReturnEmpty()
    {
        Path path = Path.Parse("");

        Assert.True(path.IsEmpty);
        Assert.Equal(0, path.Count);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Subpath from start")]
    public void Path_Subpath_ShouldSliceFromStart()
    {
        Path path = Path.Parse("a:b:c:d");
        Path sub = path.Subpath(2);

        Assert.Equal(2, sub.Count);
        Assert.Equal("c", sub[0].ToString());
        Assert.Equal("d", sub[1].ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Subpath with length")]
    public void Path_SubpathWithLength_ShouldSlice()
    {
        Path path = Path.Parse("a:b:c:d");
        Path sub = path.Subpath(1, 2);

        Assert.Equal(2, sub.Count);
        Assert.Equal("b", sub[0].ToString());
        Assert.Equal("c", sub[1].ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Combine two paths")]
    public void Path_Combine_ShouldJoinPaths()
    {
        Path left = Path.Parse("a:b");
        Path right = Path.Parse("c:d");
        Path combined = Path.Combine(left, right);

        Assert.Equal(4, combined.Count);
        Assert.Equal("a:b:c:d", combined.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Combine with overlapping prefix returns right")]
    public void Path_CombineOverlapping_ShouldReturnRight()
    {
        Path left = Path.Parse("a:b");
        Path right = Path.Parse("a:b:c");
        Path combined = Path.Combine(left, right);

        Assert.Equal("a:b:c", combined.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Plus operator combines")]
    public void Path_PlusOperator_ShouldCombine()
    {
        Path left = Path.Parse("a");
        Path right = Path.Parse("b");
        Path combined = left + right;

        Assert.Equal("a:b", combined.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Decrement operator removes first key")]
    public void Path_DecrementOperator_ShouldRemoveFirst()
    {
        Path path = Path.Parse("a:b:c");
        path--;

        Assert.Equal(2, path.Count);
        Assert.Equal("b", path[0].ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: StartsWith")]
    public void Path_StartsWith_ShouldMatch()
    {
        Path path = Path.Parse("a:b:c");
        Path prefix = Path.Parse("a:b");

        Assert.True(path.StartsWith(prefix));
        Assert.False(prefix.StartsWith(path));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Equality with same keys")]
    public void Path_Equality_ShouldMatchSameKeys()
    {
        Path p1 = Path.Parse("a:b:c");
        Path p2 = Path.Parse("a:b:c");

        Assert.True(p1.Equals(p2));
        Assert.True(p1 == p2);
        Assert.False(p1 != p2);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Inequality with different keys")]
    public void Path_Inequality_ShouldNotMatchDifferent()
    {
        Path p1 = Path.Parse("a:b:c");
        Path p2 = Path.Parse("a:b:d");

        Assert.False(p1.Equals(p2));
        Assert.True(p1 != p2);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Case-insensitive equality")]
    public void Path_CaseInsensitiveEquality_ShouldMatch()
    {
        Path p1 = Path.Parse("Key1:Key2");
        Path p2 = Path.Parse("key1:key2");

        Assert.True(p1.Equals(p2, KeyComparison.OrdinalIgnoreCase));
        Assert.False(p1.Equals(p2, KeyComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: GetHashCode consistency")]
    public void Path_GetHashCode_ShouldBeConsistent()
    {
        Path p1 = Path.Parse("a:b:c");
        Path p2 = Path.Parse("a:b:c");

        Assert.Equal(p1.GetHashCode(), p2.GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Equals with object")]
    public void Path_EqualsObject_ShouldWork()
    {
        Path p1 = Path.Parse("a:b");
        object p2 = Path.Parse("a:b");

        Assert.True(p1.Equals(p2));
        Assert.False(p1.Equals((object)"a:b"));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Implicit from Key")]
    public void Path_ImplicitFromKey_ShouldWork()
    {
        Key key = new Key("test");
        Path path = key;

        Assert.Equal(1, path.Count);
        Assert.Equal("test", path[0].ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: Leading and trailing delimiters are trimmed")]
    public void Path_LeadingTrailingDelimiters_ShouldBeTrimmed()
    {
        Path path = Path.Parse(":key1:key2:");

        Assert.Equal(2, path.Count);
        Assert.Equal("key1", path[0].ToString());
        Assert.Equal("key2", path[1].ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Path: JSON serialization roundtrip")]
    public void Path_JsonSerialization_ShouldRoundtrip()
    {
        Path path = Path.Parse("key1:key2");
        string json = JsonSerializer.Serialize(new { path });

        Assert.Contains("key1:key2", json);
    }
}
