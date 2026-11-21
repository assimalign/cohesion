using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpDecimalTests
{
    [Fact(DisplayName = "Cohesion Test [Amqp] - Decimal32: Should keep its bits, compare by them and print them as hexadecimal")]
    public void Decimal32_WithBits_ShouldKeepAndCompareBits()
    {
        // Act
        AmqpDecimal32 value = new(0x22500000u);

        // Assert
        value.Bits.ShouldBe(0x22500000u);
        value.ToString().ShouldBe("0x22500000");
        (value == new AmqpDecimal32(0x22500000u)).ShouldBeTrue();
        (value != new AmqpDecimal32(0x22500001u)).ShouldBeTrue();
        value.GetHashCode().ShouldBe(new AmqpDecimal32(0x22500000u).GetHashCode());
        value.Equals((object)new AmqpDecimal32(0x22500000u)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Decimal64: Should keep its bits, compare by them and print them as hexadecimal")]
    public void Decimal64_WithBits_ShouldKeepAndCompareBits()
    {
        // Act
        AmqpDecimal64 value = new(0x2238000000000001UL);

        // Assert
        value.Bits.ShouldBe(0x2238000000000001UL);
        value.ToString().ShouldBe("0x2238000000000001");
        (value == new AmqpDecimal64(0x2238000000000001UL)).ShouldBeTrue();
        (value != new AmqpDecimal64(0x2238000000000000UL)).ShouldBeTrue();
        value.GetHashCode().ShouldBe(new AmqpDecimal64(0x2238000000000001UL).GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Decimal128: Should keep its bits, compare by them and print them as hexadecimal")]
    public void Decimal128_WithBits_ShouldKeepAndCompareBits()
    {
        // Arrange
        UInt128 bits = new(0x2208000000000000UL, 1UL);

        // Act
        AmqpDecimal128 value = new(bits);

        // Assert
        value.Bits.ShouldBe(bits);
        value.ToString().ShouldBe("0x22080000000000000000000000000001");
        (value == new AmqpDecimal128(bits)).ShouldBeTrue();
        (value != new AmqpDecimal128(bits + 1)).ShouldBeTrue();
        value.GetHashCode().ShouldBe(new AmqpDecimal128(bits).GetHashCode());
    }
}
