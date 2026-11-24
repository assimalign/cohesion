using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpDescribedValueTests
{
    public static TheoryData<object, object> Descriptors => new()
    {
        { new AmqpSymbol("x-opt-a"), new AmqpSymbol("x-opt-a") },
        { 0x77UL, 0x77UL },
        { "x-opt-b", new AmqpSymbol("x-opt-b") }
    };

    public static TheoryData<string, object> InvalidDescriptors => new()
    {
        { "an int", 7 },
        { "a uint", 7u },
        { "a long", 7L },
        { "a uuid", Guid.Empty },
        { "a default symbol, which holds no name", default(AmqpSymbol) }
    };

    [Theory(DisplayName = "Cohesion Test [Amqp] - Constructor: Should keep a symbol or ulong descriptor and convert a string to a symbol")]
    [MemberData(nameof(Descriptors))]
    public void Constructor_WithValidDescriptor_ShouldKeepDescriptor(object descriptor, object expected)
    {
        // Act
        AmqpDescribedValue described = new(descriptor, "value");

        // Assert
        described.Descriptor.ShouldBe(expected);
        described.Value.ShouldBe("value");
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - Constructor: Should refuse a descriptor that is neither a symbol nor an unsigned long")]
    [MemberData(nameof(InvalidDescriptors))]
    public void Constructor_WithInvalidDescriptor_ShouldThrowArgumentException(string scenario, object descriptor)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new AmqpDescribedValue(descriptor, null), scenario);

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldStartWith(ErrorMessages.DescriptorTypeInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Constructor: Should refuse a string descriptor that is not seven-bit ASCII")]
    public void Constructor_WithNonAsciiStringDescriptor_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new AmqpDescribedValue("café", null));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldStartWith(ErrorMessages.SymbolInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Constructor: Should refuse a null descriptor and allow a null value")]
    public void Constructor_WithNullDescriptor_ShouldThrowArgumentNullException()
    {
        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => new AmqpDescribedValue(null!, null));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        new AmqpDescribedValue(1UL, null).Value.ShouldBeNull();
    }
}
