using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration.Tests;

public class KeyComparerTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - KeyComparer: FromComparison ordinal")]
    public void KeyComparer_FromComparison_ShouldReturnComparer()
    {
        var comparer = KeyComparer.FromComparison(KeyComparison.Ordinal);

        Assert.NotNull(comparer);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - KeyComparer: FromComparison returns cached singleton")]
    public void KeyComparer_FromComparison_ShouldReturnCachedSingleton()
    {
        Assert.Same(KeyComparer.OrdinalIgnoreCase, KeyComparer.FromComparison(KeyComparison.OrdinalIgnoreCase));
        Assert.Same(KeyComparer.InvariantCulture, KeyComparer.FromComparison(KeyComparison.InvariantCulture));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - KeyComparer: Ordinal compare")]
    public void KeyComparer_OrdinalCompare_ShouldWork()
    {
        var comparer = KeyComparer.Ordinal;
        Key a = new Key("alpha");
        Key b = new Key("beta");

        Assert.True(comparer.Compare(a, b) < 0);
        Assert.True(comparer.Compare(b, a) > 0);
        Assert.Equal(0, comparer.Compare(a, new Key("alpha")));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - KeyComparer: OrdinalIgnoreCase equality")]
    public void KeyComparer_OrdinalIgnoreCase_ShouldBeEqual()
    {
        var comparer = KeyComparer.OrdinalIgnoreCase;
        Key k1 = new Key("Test");
        Key k2 = new Key("test");

        Assert.True(((System.Collections.Generic.IEqualityComparer<Key>)comparer).Equals(k1, k2));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - KeyComparer: Ordinal case-sensitive inequality")]
    public void KeyComparer_Ordinal_ShouldBeCaseSensitive()
    {
        var comparer = KeyComparer.Ordinal;
        Key k1 = new Key("Test");
        Key k2 = new Key("test");

        Assert.False(((System.Collections.Generic.IEqualityComparer<Key>)comparer).Equals(k1, k2));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - KeyComparer: Path equality")]
    public void KeyComparer_PathEquality_ShouldWork()
    {
        var comparer = KeyComparer.FromComparison(KeyComparison.OrdinalIgnoreCase);
        Path p1 = Path.Parse("A:B");
        Path p2 = Path.Parse("a:b");

        Assert.True(((System.Collections.Generic.IEqualityComparer<Path>)comparer).Equals(p1, p2));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - KeyComparer: Path dictionary lookup honors comparison")]
    public void KeyComparer_PathDictionaryLookup_ShouldHonorComparison()
    {
        Dictionary<Path, string> values = new(KeyComparer.OrdinalIgnoreCase)
        {
            ["Section:Key"] = "value"
        };

        Assert.True(values.ContainsKey("section:key"));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - KeyComparer: Static instances")]
    public void KeyComparer_StaticInstances_ShouldExist()
    {
        Assert.NotNull(KeyComparer.Ordinal);
        Assert.NotNull(KeyComparer.OrdinalIgnoreCase);
        Assert.NotNull(KeyComparer.CurrentCulture);
        Assert.NotNull(KeyComparer.CurrentCultureIgnoreCase);
        Assert.NotNull(KeyComparer.InvariantCulture);
        Assert.NotNull(KeyComparer.InvariantCultureIgnoreCase);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - KeyComparer: GetHashCode equal for equal keys")]
    public void KeyComparer_GetHashCode_ShouldBeEqualForEqualKeys()
    {
        var comparer = KeyComparer.OrdinalIgnoreCase;
        Key k1 = new Key("Test");
        Key k2 = new Key("test");

        Assert.Equal(comparer.GetHashCode(k1), comparer.GetHashCode(k2));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - KeyComparer: AlternateLookup equality")]
    public void KeyComparer_AlternateLookup_ShouldWork()
    {
        var comparer = KeyComparer.OrdinalIgnoreCase;
        Key key = new Key("TestKey");
        ReadOnlySpan<char> span = "testkey".AsSpan();

        Assert.True(comparer.Equals(span, key));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - KeyComparer: AlternateLookup dictionary honors comparison")]
    public void KeyComparer_AlternateLookupDictionary_ShouldHonorComparison()
    {
        Dictionary<Key, string> values = new(KeyComparer.OrdinalIgnoreCase)
        {
            ["TestKey"] = "value"
        };
        Dictionary<Key, string>.AlternateLookup<ReadOnlySpan<char>> lookup = values.GetAlternateLookup<ReadOnlySpan<char>>();

        Assert.True(lookup.TryGetValue("testkey".AsSpan(), out string? value));
        Assert.Equal("value", value);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - KeyComparer: Create from span")]
    public void KeyComparer_Create_ShouldCreateKeyFromSpan()
    {
        var comparer = KeyComparer.Ordinal;
        ReadOnlySpan<char> span = "hello".AsSpan();
        Key key = comparer.Create(span);

        Assert.Equal("hello", key.ToString());
    }
}
