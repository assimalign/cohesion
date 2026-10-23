using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Configuration.Tests;

public class KeyOperatorTests
{
    [Theory(DisplayName = "Cohesion Test [Configuration] - Nullable key operators obey value equality")]
    [InlineData(null, null, true)]
    [InlineData(null, "key", false)]
    [InlineData("key", null, false)]
    [InlineData("key", "key", true)]
    [InlineData("key", "other", false)]
    [InlineData("key", "KEY", false)]
    [InlineData("", "", true)]
    public void Compare_NullableKeys_ShouldFollowValueEquality(string? left, string? right, bool equal)
    {
        // Arrange
        Key? leftKey = left is null ? (Key?)null : new Key(left);
        Key? rightKey = right is null ? (Key?)null : new Key(right);

        // Act / Assert
        (leftKey == rightKey).ShouldBe(equal);
        (leftKey != rightKey).ShouldBe(!equal);
        if (rightKey.HasValue)
        {
            (leftKey == rightKey.Value).ShouldBe(equal);
            (leftKey != rightKey.Value).ShouldBe(!equal);
        }

        if (leftKey.HasValue)
        {
            (leftKey.Value == rightKey).ShouldBe(equal);
            (leftKey.Value != rightKey).ShouldBe(!equal);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - Nullable default and empty keys compare equally")]
    public void Compare_DefaultAndEmptyKeys_ShouldBeEqual()
    {
        // Arrange
        Key? left = default(Key);
        Key? right = new Key(string.Empty);

        // Act / Assert
        (left == right).ShouldBeTrue();
        (left != right).ShouldBeFalse();
    }
}
