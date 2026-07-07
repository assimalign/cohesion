using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

public class IndexKeyTests
{
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
