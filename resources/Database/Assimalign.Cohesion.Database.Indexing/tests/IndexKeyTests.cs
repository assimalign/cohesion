using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

public class IndexKeyTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Indexing] - IndexKey: collation-equal strings share bytes and unique-lock hashes")]
    [InlineData(2, "Alice", "alice")]
    [InlineData(3, "ÁLICE", "alice")]
    [InlineData(3, "E\u0301", "é")]
    public void FromString_CollationEqualValues_ShouldHaveIdenticalKeys(byte id, string first, string second)
    {
        var left = IndexKey.FromString(first, Collation.FromId(id));
        var right = IndexKey.FromString(second, Collation.FromId(id));

        left.Equals(right).ShouldBeTrue();
        left.Encoded.ToArray().ShouldBe(right.Encoded.ToArray());
        left.CompareTo(right).ShouldBe(0);
        left.GetHashCode().ShouldBe(right.GetHashCode());
        left.Hash().ShouldBe(right.Hash());
    }

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - IndexKey: binary strings remain case-sensitive")]
    public void FromString_BinaryValues_ShouldKeepDifferentCaseDistinct()
        => IndexKey.FromString("Alice", Collation.Binary).Equals(IndexKey.FromString("alice", Collation.Binary)).ShouldBeFalse();

    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - IndexKey: non-index-backed collation is rejected")]
    public void FromString_Invariant_ShouldReject()
        => Should.Throw<DatabaseTypeException>(() => IndexKey.FromString("Alice", Collation.Invariant));

    [Fact(DisplayName = "Cohesion Test [Database] - IndexKey: Signed encoding preserves numeric order")]
    public void FromInt64_MixedSignValues_ShouldPreserveNumericOrder()
    {
        // Arrange
        var negative = IndexKey.FromInt64(-42);
        var zero = IndexKey.FromInt64(0);
        var positive = IndexKey.FromInt64(42);
        var minimum = IndexKey.FromInt64(long.MinValue);
        var maximum = IndexKey.FromInt64(long.MaxValue);

        // Assert
        minimum.CompareTo(negative).ShouldBeLessThan(0);
        negative.CompareTo(zero).ShouldBeLessThan(0);
        zero.CompareTo(positive).ShouldBeLessThan(0);
        positive.CompareTo(maximum).ShouldBeLessThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - IndexKey: Unsigned encoding preserves numeric order")]
    public void FromUInt64_AscendingValues_ShouldPreserveNumericOrder()
    {
        // Arrange
        var low = IndexKey.FromUInt64(1);
        var middle = IndexKey.FromUInt64(256);
        var high = IndexKey.FromUInt64(ulong.MaxValue);

        // Assert
        low.CompareTo(middle).ShouldBeLessThan(0);
        middle.CompareTo(high).ShouldBeLessThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - IndexKey: Equal values produce equal keys")]
    public void Equals_SameValue_ShouldBeEqualWithSameHashCode()
    {
        // Arrange
        var first = IndexKey.FromInt64(1234);
        var second = IndexKey.FromInt64(1234);

        // Assert
        first.Equals(second).ShouldBeTrue();
        first.GetHashCode().ShouldBe(second.GetHashCode());
        first.CompareTo(second).ShouldBe(0);
    }
}
