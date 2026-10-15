using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Configuration.Tests;

public class KeyHashCodeTests
{
    [Theory(DisplayName = "Cohesion Test [Configuration] - GetHashCode: Default and constructed-empty keys hash equally")]
    [InlineData(null)]
    [InlineData("")]
    public void GetHashCode_DefaultAndConstructedEmpty_ShouldMatch(string? value)
    {
        // Arrange
        Key defaultKey = default;
        Key emptyKey = new(value!);

        // Act
        int defaultHash = defaultKey.GetHashCode();
        int emptyHash = emptyKey.GetHashCode();

        // Assert
        defaultKey.Equals(emptyKey).ShouldBeTrue();
        defaultHash.ShouldBe(emptyHash);
    }

    [Theory(DisplayName = "Cohesion Test [Configuration] - GetHashCode: Comparers preserve empty-key hash identity")]
    [InlineData(KeyComparison.Ordinal)]
    [InlineData(KeyComparison.OrdinalIgnoreCase)]
    [InlineData(KeyComparison.CurrentCulture)]
    [InlineData(KeyComparison.CurrentCultureIgnoreCase)]
    [InlineData(KeyComparison.InvariantCulture)]
    [InlineData(KeyComparison.InvariantCultureIgnoreCase)]
    public void GetHashCode_DefaultAndConstructedEmptyWithComparison_ShouldMatch(KeyComparison comparison)
    {
        // Arrange
        Key defaultKey = default;
        Key emptyKey = new(string.Empty);
        KeyComparer comparer = KeyComparer.FromComparison(comparison);

        // Act
        int defaultHash = defaultKey.GetHashCode(comparison);
        int emptyHash = emptyKey.GetHashCode(comparison);
        int alternateHash = comparer.GetHashCode(ReadOnlySpan<char>.Empty);

        // Assert
        comparer.Equals(defaultKey, emptyKey).ShouldBeTrue();
        defaultHash.ShouldBe(emptyHash);
        comparer.GetHashCode(defaultKey).ShouldBe(alternateHash);
    }

    [Theory(DisplayName = "Cohesion Test [Configuration] - Dictionary: Default and constructed-empty keys address one entry")]
    [InlineData(false)]
    [InlineData(true)]
    public void TryGetValue_DefaultAndConstructedEmpty_ShouldResolveSameEntry(bool insertDefault)
    {
        // Arrange
        Key defaultKey = default;
        Key emptyKey = new(string.Empty);
        Key inserted = insertDefault ? defaultKey : emptyKey;
        Key equivalent = insertDefault ? emptyKey : defaultKey;
        Dictionary<Key, string> values = new() { [inserted] = "value" };

        // Act
        bool found = values.TryGetValue(equivalent, out string? value);
        values[equivalent] = "updated";

        // Assert
        found.ShouldBeTrue();
        value.ShouldBe("value");
        values.Count.ShouldBe(1);
        values[inserted].ShouldBe("updated");
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - AlternateLookup: Empty span resolves an ordinal default key")]
    public void GetAlternateLookup_OrdinalDefaultKey_ShouldResolveEmptySpan()
    {
        // Arrange
        Dictionary<Key, string> values = new(KeyComparer.Ordinal) { [default] = "value" };
        Dictionary<Key, string>.AlternateLookup<ReadOnlySpan<char>> lookup =
            values.GetAlternateLookup<ReadOnlySpan<char>>();

        // Act
        bool found = lookup.TryGetValue(ReadOnlySpan<char>.Empty, out string? value);

        // Assert
        found.ShouldBeTrue();
        value.ShouldBe("value");
    }
}
