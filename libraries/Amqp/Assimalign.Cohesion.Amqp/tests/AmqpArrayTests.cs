using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpArrayTests
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
        { "a long", 7L },
        { "a uuid", Guid.Empty },
        { "a default symbol, which holds no name", default(AmqpSymbol) }
    };

    [Theory(DisplayName = "Cohesion Test [Amqp] - Constructor: Should keep an array's element type and a symbol or ulong descriptor, converting a string to a symbol")]
    [MemberData(nameof(Descriptors))]
    public void Constructor_WithValidDescriptor_ShouldKeepDescriptor(object descriptor, object expected)
    {
        // Act
        AmqpArray array = new(AmqpPrimitiveType.String, ["a", "b"], descriptor);

        // Assert
        array.ElementType.ShouldBe(AmqpPrimitiveType.String);
        array.Descriptor.ShouldBe(expected);
        array.Elements.ShouldBe(["a", "b"]);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Constructor: Should leave the descriptor null when the elements are not described")]
    public void Constructor_WithoutDescriptor_ShouldHaveNoDescriptor()
    {
        // Act
        AmqpArray array = new(AmqpPrimitiveType.UByte, [(byte)1]);

        // Assert
        array.Descriptor.ShouldBeNull();
        array.Elements.ShouldHaveSingleItem().ShouldBe((byte)1);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - Constructor: Should refuse a descriptor that is neither a symbol nor an unsigned long")]
    [MemberData(nameof(InvalidDescriptors))]
    public void Constructor_WithInvalidDescriptor_ShouldThrowArgumentException(string scenario, object descriptor)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new AmqpArray(AmqpPrimitiveType.Int, [1], descriptor), scenario);

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldStartWith(ErrorMessages.DescriptorTypeInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Constructor: Should refuse a string descriptor that is not seven-bit ASCII")]
    public void Constructor_WithNonAsciiStringDescriptor_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new AmqpArray(AmqpPrimitiveType.Int, [1], "café"));

        // Assert
        exception.ParamName.ShouldBe("descriptor");
        exception.Message.ShouldStartWith(ErrorMessages.SymbolInvalid);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - Constructor: Should refuse an element type that names no AMQP type")]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(-1)]
    public void Constructor_WithUndefinedElementType_ShouldThrowArgumentOutOfRangeException(int elementType)
    {
        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(() => new AmqpArray((AmqpPrimitiveType)elementType, []));

        // Assert
        exception.ParamName.ShouldBe("elementType");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Constructor: Should refuse null elements")]
    public void Constructor_WithNullElements_ShouldThrowArgumentNullException()
    {
        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => new AmqpArray(AmqpPrimitiveType.Int, null!));

        // Assert
        exception.ParamName.ShouldBe("elements");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Constructor: Should copy the elements, so a later change to the source leaves the array as it was")]
    public void Constructor_WithElements_ShouldCopyThem()
    {
        // Arrange
        List<object?> source = [1, 2];

        // Act
        AmqpArray array = new(AmqpPrimitiveType.Int, source);
        source[0] = 3;
        source.Add(4);

        // Assert
        array.Elements.ShouldBe([1, 2]);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Elements: Should be read-only")]
    public void Elements_OnArray_ShouldBeReadOnly()
    {
        // Arrange
        AmqpArray array = new(AmqpPrimitiveType.Int, [1, 2]);

        // Act
        IList<object?> elements = array.Elements.ShouldBeAssignableTo<IList<object?>>().ShouldNotBeNull();

        // Assert
        elements.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => elements[0] = 3);
        array.Elements.ShouldBe([1, 2]);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - AmqpPrimitiveType: Should number each type as its section in Part 1, section 1.6, with no type at zero")]
    public void AmqpPrimitiveType_OnEveryValue_ShouldNumberItsSection()
    {
        // Act
        int[] values = [.. Enum.GetValues<AmqpPrimitiveType>().Select(value => (int)value)];

        // Assert
        values.ShouldBe(Enumerable.Range(1, 24));
        ((int)AmqpPrimitiveType.Null).ShouldBe(1);
        ((int)AmqpPrimitiveType.Array).ShouldBe(24);
        Enum.IsDefined(default(AmqpPrimitiveType)).ShouldBeFalse();
    }
}
