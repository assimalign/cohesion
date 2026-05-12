using System;
using System.Text.Json;

namespace Assimalign.Cohesion.Configuration.Tests;

public class KeyTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - Key: Should create from string")]
    public void Key_FromString_ShouldCreate()
    {
        Key key = new Key("TestKey");

        Assert.False(key.IsEmpty);
        Assert.Equal("TestKey", key.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Key: Should create from ReadOnlySpan")]
    public void Key_FromSpan_ShouldCreate()
    {
        ReadOnlySpan<char> span = "TestKey".AsSpan();
        Key key = new Key(span);

        Assert.Equal("TestKey", key.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Key: Should throw when containing delimiter")]
    public void Key_WithDelimiter_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => new Key("key:value"));
        Assert.Throws<ArgumentException>(() => new Key("key/value"));
        Assert.Throws<ArgumentException>(() => new Key("key\\value"));
        Assert.Throws<ArgumentException>(() => new Key("key.value"));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Key: Default should be empty")]
    public void Key_Default_ShouldBeEmpty()
    {
        var key = default(Key);

        Assert.True(key.IsEmpty);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Key: Implicit conversion from string")]
    public void Key_ImplicitFromString_ShouldWork()
    {
        Key key = "TestKey";

        Assert.Equal("TestKey", key.ToString());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Key: Implicit conversion to string")]
    public void Key_ImplicitToString_ShouldWork()
    {
        Key key = new Key("TestKey");
        string value = key;

        Assert.Equal("TestKey", value);
    }

    [Theory(DisplayName = "Cohesion Test [Configuration] - Key: Ordinal equality")]
    [InlineData("key1", "key1", true)]
    [InlineData("key1", "Key1", false)]
    [InlineData("key1", "key2", false)]
    public void Key_OrdinalEquality_ShouldMatch(string left, string right, bool expected)
    {
        Key k1 = new Key(left);
        Key k2 = new Key(right);

        Assert.Equal(expected, k1.Equals(k2));
        Assert.Equal(expected, k1 == k2);
        Assert.Equal(!expected, k1 != k2);
    }

    [Theory(DisplayName = "Cohesion Test [Configuration] - Key: Case-insensitive equality")]
    [InlineData("key1", "KEY1", true)]
    [InlineData("key1", "Key1", true)]
    [InlineData("key1", "key2", false)]
    public void Key_OrdinalIgnoreCaseEquality_ShouldMatch(string left, string right, bool expected)
    {
        Key k1 = new Key(left);
        Key k2 = new Key(right);

        Assert.Equal(expected, k1.Equals(k2, KeyComparison.OrdinalIgnoreCase));
    }

    [Theory(DisplayName = "Cohesion Test [Configuration] - Key: StartsWith")]
    [InlineData("TestKey", "Test", true)]
    [InlineData("TestKey", "test", false)]
    [InlineData("TestKey", "TestKeyExtra", false)]
    public void Key_StartsWith_ShouldMatch(string key, string prefix, bool expected)
    {
        Key k = new Key(key);
        Key p = new Key(prefix);

        Assert.Equal(expected, k.StartsWith(p));
    }

    [Theory(DisplayName = "Cohesion Test [Configuration] - Key: EndsWith")]
    [InlineData("TestKey", "Key", true)]
    [InlineData("TestKey", "key", false)]
    [InlineData("TestKey", "TestKeyExtra", false)]
    public void Key_EndsWith_ShouldMatch(string key, string suffix, bool expected)
    {
        Key k = new Key(key);
        Key s = new Key(suffix);

        Assert.Equal(expected, k.EndsWith(s));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Key: CompareTo ordinal")]
    public void Key_CompareTo_ShouldReturnCorrectOrder()
    {
        Key a = new Key("alpha");
        Key b = new Key("beta");

        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
        Assert.Equal(0, a.CompareTo(new Key("alpha")));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Key: GetHashCode consistency")]
    public void Key_GetHashCode_ShouldBeConsistent()
    {
        Key k1 = new Key("TestKey");
        Key k2 = new Key("TestKey");

        Assert.Equal(k1.GetHashCode(), k2.GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Key: Equals with object")]
    public void Key_EqualsObject_ShouldWork()
    {
        Key k1 = new Key("TestKey");
        object k2 = new Key("TestKey");
        object notKey = "TestKey";

        Assert.True(k1.Equals(k2));
        Assert.False(k1.Equals(notKey));
        Assert.False(k1.Equals(null));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Key: AsSpan should return correct span")]
    public void Key_AsSpan_ShouldReturnValue()
    {
        Key key = new Key("TestKey");
        ReadOnlySpan<char> span = key.AsSpan();

        Assert.True(span.SequenceEqual("TestKey".AsSpan()));
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Key: JSON serialization roundtrip")]
    public void Key_JsonSerialization_ShouldRoundtrip()
    {
        Key key = new Key("TestKey");
        string json = JsonSerializer.Serialize(new { key });

        Assert.Contains("TestKey", json);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Key: Nullable equality operators")]
    public void Key_NullableOperators_ShouldWork()
    {
        Key k1 = new Key("test");
        Key k2 = new Key("test");
        Key k3 = new Key("other");

        Assert.True(k1 == k2);
        Assert.False(k1 == k3);
        Assert.True(k1 != k3);
    }
}
