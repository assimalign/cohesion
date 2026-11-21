using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpDeliveryTagTests
{
    [Fact(DisplayName = "Cohesion Test [Amqp] - Create: Should copy the bytes, so later changes to the source do not reach the tag")]
    public void Create_WithBytes_ShouldCopyValue()
    {
        // Arrange
        byte[] source = [1, 2, 3];

        // Act
        AmqpDeliveryTag tag = AmqpDeliveryTag.Create(source);
        source[0] = 9;

        // Assert
        tag.Value.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        tag.Length.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Create: Should accept a tag of exactly 32 bytes")]
    public void Create_AtMaxLength_ShouldCreateTag()
    {
        // Act
        AmqpDeliveryTag tag = AmqpDeliveryTag.Create(new byte[AmqpDeliveryTag.MaxLength]);

        // Assert
        tag.Length.ShouldBe(32);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Create: Should refuse a tag longer than 32 bytes")]
    public void Create_OverMaxLength_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpDeliveryTag.Create(new byte[33]));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldStartWith(ErrorMessages.DeliveryTagTooLong);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - TryCreate: Should return false and the empty tag for more than 32 bytes")]
    public void TryCreate_OverMaxLength_ShouldReturnFalse()
    {
        // Act
        bool created = AmqpDeliveryTag.TryCreate(new byte[33], out AmqpDeliveryTag tag);

        // Assert
        created.ShouldBeFalse();
        tag.Length.ShouldBe(0);
        AmqpDeliveryTag.IsValid(new byte[32]).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - Create: Should encode an integer as its shortest big-endian bytes, at least one")]
    [InlineData(0UL, "00")]
    [InlineData(255UL, "ff")]
    [InlineData(256UL, "0100")]
    [InlineData(ulong.MaxValue, "ffffffffffffffff")]
    public void Create_WithInteger_ShouldUseShortestBigEndianBytes(ulong value, string expected)
    {
        // Act
        AmqpDeliveryTag tag = AmqpDeliveryTag.Create(value);

        // Assert
        tag.ToString().ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Equals: Should compare tags by their bytes, leading zeros included")]
    public void Equals_WithSameBytes_ShouldBeEqualAndHashAlike()
    {
        // Arrange
        AmqpDeliveryTag tag = AmqpDeliveryTag.Create(new byte[] { 0, 1 });
        AmqpDeliveryTag same = AmqpDeliveryTag.Create(new byte[] { 0, 1 });
        AmqpDeliveryTag shorter = AmqpDeliveryTag.Create(1UL);

        // Act
        bool equal = tag == same;

        // Assert
        equal.ShouldBeTrue();
        tag.GetHashCode().ShouldBe(same.GetHashCode());
        (tag != shorter).ShouldBeTrue();
        tag.Equals((object)same).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Default: Should be the empty tag")]
    public void Default_OnTag_ShouldEqualEmptyTag()
    {
        // Arrange
        AmqpDeliveryTag empty = AmqpDeliveryTag.Create(ReadOnlySpan<byte>.Empty);

        // Act
        AmqpDeliveryTag tag = default;

        // Assert
        tag.ShouldBe(empty);
        tag.Length.ShouldBe(0);
        tag.ToString().ShouldBe(string.Empty);
        tag.GetHashCode().ShouldBe(empty.GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - GetHashCode: Should let an equal tag find an unsettled delivery in a dictionary")]
    public void GetHashCode_WithEqualTag_ShouldFindDictionaryEntry()
    {
        // Arrange
        Dictionary<AmqpDeliveryTag, string> unsettled = new()
        {
            [AmqpDeliveryTag.Create(7UL)] = "seven"
        };

        // Act
        bool found = unsettled.TryGetValue(AmqpDeliveryTag.Create(new byte[] { 7 }), out string? delivery);

        // Assert
        found.ShouldBeTrue();
        delivery.ShouldBe("seven");
    }
}
