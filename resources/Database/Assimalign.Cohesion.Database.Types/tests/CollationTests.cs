using System;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Types.Tests;

/// <summary>
/// Tests for explicit collation identity and comparison semantics (#854).
/// </summary>
public class CollationTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Types] - Collation: identifiers are stable and resolvable")]
    public void FromId_KnownIdentifiers_ShouldResolve()
    {
        Collation.FromId(0).ShouldBeSameAs(Collation.Binary);
        Collation.FromId(1).ShouldBeSameAs(Collation.Invariant);
        Should.Throw<DatabaseTypeException>(() => Collation.FromId(200));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Collation: binary compares by code point, not UTF-16 units")]
    public void BinaryCompare_SupplementaryPlane_ShouldOrderByCodePoint()
    {
        // U+1F600 (😀, surrogate pair in UTF-16) must order above U+FFFD, matching
        // UTF-8 byte order — string.CompareOrdinal would order it below.
        Collation.Binary.Compare("\U0001F600", "�").ShouldBeGreaterThan(0);
        Collation.Binary.Compare("a", "b").ShouldBeLessThan(0);
        Collation.Binary.Compare("same", "same").ShouldBe(0);
        Collation.Binary.Compare("ab", "abc").ShouldBeLessThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Types] - Collation: invariant comparison is culture-stable and case-sensitive")]
    public void InvariantCompare_MixedCase_ShouldFollowInvariantCulture()
    {
        Collation.Invariant.Compare("apple", "Apple").ShouldBeLessThan(0);
        Collation.Invariant.Compare("apple", "banana").ShouldBeLessThan(0);
        Collation.Invariant.Compare("same", "same").ShouldBe(0);
    }
}
