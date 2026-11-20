using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpSymbolTests
{
    [Theory(DisplayName = "Cohesion Test [Amqp] - Constructor: Should accept seven-bit ASCII text, including the empty string")]
    [InlineData("")]
    [InlineData("amqp:not-found")]
    [InlineData("x-opt-\u007f")]
    public void Constructor_WithAsciiText_ShouldKeepValue(string value)
    {
        // Act
        AmqpSymbol symbol = new(value);

        // Assert
        symbol.Value.ShouldBe(value);
        symbol.ToString().ShouldBe(value);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - Constructor: Should refuse text outside seven-bit ASCII")]
    [InlineData("café")]
    [InlineData("\u0080")]
    [InlineData("smile-😀")]
    public void Constructor_WithNonAsciiText_ShouldThrowArgumentException(string value)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new AmqpSymbol(value));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldStartWith(ErrorMessages.SymbolInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Constructor: Should refuse a null value")]
    public void Constructor_WithNull_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new AmqpSymbol(null!));

        // Assert
        exception.ParamName.ShouldBe("value");
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - TryCreate: Should return false and the default symbol for null or non-ASCII text")]
    [InlineData(null)]
    [InlineData("café")]
    public void TryCreate_WithInvalidText_ShouldReturnFalse(string? value)
    {
        // Act
        bool created = AmqpSymbol.TryCreate(value, out AmqpSymbol symbol);

        // Assert
        created.ShouldBeFalse();
        symbol.Value.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Equals: Should compare ordinally, so case matters")]
    public void Equals_WithDifferentCase_ShouldReturnFalse()
    {
        // Arrange
        AmqpSymbol lower = new("plain");
        AmqpSymbol upper = new("PLAIN");

        // Act
        bool equal = lower == upper;

        // Assert
        equal.ShouldBeFalse();
        lower.Equals(new AmqpSymbol("plain")).ShouldBeTrue();
        lower.GetHashCode().ShouldBe(new AmqpSymbol("plain").GetHashCode());
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - CompareTo: Should order ordinally, upper case before lower case")]
    public void CompareTo_WithMixedCase_ShouldOrderOrdinally()
    {
        // Arrange
        AmqpSymbol upper = new("Z");
        AmqpSymbol lower = new("a");

        // Act
        int comparison = upper.CompareTo(lower);

        // Assert
        comparison.ShouldBeLessThan(0);
        (lower > upper).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Default: Should compare, hash and print safely, and differ from the empty symbol")]
    public void Default_OnSymbol_ShouldCompareHashAndPrintSafely()
    {
        // Arrange
        AmqpSymbol symbol = default;

        // Act
        string text = symbol.ToString();

        // Assert
        text.ShouldBe(string.Empty);
        symbol.Value.ShouldBeNull();
        symbol.GetHashCode().ShouldBe(0);
        symbol.Equals(default(AmqpSymbol)).ShouldBeTrue();
        symbol.Equals(new AmqpSymbol(string.Empty)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Implicit: Should convert from and to a string, refusing non-ASCII text")]
    public void Implicit_WithString_ShouldConvertBothWays()
    {
        // Act
        AmqpSymbol symbol = "amqp:accepted:list";
        string text = symbol;

        // Assert
        text.ShouldBe("amqp:accepted:list");
        Should.Throw<ArgumentException>(() => (AmqpSymbol)"café");
    }
}
