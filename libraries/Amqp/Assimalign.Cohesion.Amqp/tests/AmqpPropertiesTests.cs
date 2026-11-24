using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpPropertiesTests
{
    public static TheoryData<string, object?> Identifiers => new()
    {
        { "null", null },
        { "a ulong", 7UL },
        { "a uuid", Guid.Parse("8f5c2c39-7b0e-4f1e-9a46-3f6f2bd0c1a2") },
        { "a string", "order-7" },
        { "a byte array", new byte[] { 1, 2 } },
        { "binary memory", new ReadOnlyMemory<byte>(new byte[] { 3, 4 }) }
    };

    public static TheoryData<string, object> InvalidIdentifiers => new()
    {
        { "an int", 7 },
        { "a uint", 7u },
        { "a long", 7L },
        { "a symbol", new AmqpSymbol("order-7") },
        { "an sbyte array, which passes a byte[] type test", new sbyte[] { 1, 2 } },
        { "an int array", new int[] { 1 } },
        { "an object", new object() }
    };

    [Theory(DisplayName = "Cohesion Test [Amqp] - MessageId: Should accept the four identifier types and null")]
    [MemberData(nameof(Identifiers))]
    public void MessageId_WithIdentifierType_ShouldKeepValue(string scenario, object? identifier)
    {
        // Arrange
        AmqpProperties properties = new();

        // Act
        properties.MessageId = identifier;
        properties.CorrelationId = identifier;

        // Assert
        properties.MessageId.ShouldBeSameAs(identifier, scenario);
        properties.CorrelationId.ShouldBeSameAs(identifier, scenario);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - MessageId: Should refuse a type outside the four identifier types")]
    [MemberData(nameof(InvalidIdentifiers))]
    public void MessageId_WithOtherType_ShouldThrowArgumentException(string scenario, object identifier)
    {
        // Arrange
        AmqpProperties properties = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => properties.MessageId = identifier, scenario);

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldStartWith(ErrorMessages.MessageIdTypeInvalid);
        Should.Throw<ArgumentException>(() => properties.CorrelationId = identifier, scenario);
        properties.MessageId.ShouldBeNull();
        properties.CorrelationId.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Constructor: Should leave every property unset")]
    public void Constructor_OnProperties_ShouldLeaveEveryPropertyUnset()
    {
        // Act
        AmqpProperties properties = new();

        // Assert
        properties.MessageId.ShouldBeNull();
        properties.UserId.ShouldBeNull();
        properties.To.ShouldBeNull();
        properties.Subject.ShouldBeNull();
        properties.ReplyTo.ShouldBeNull();
        properties.CorrelationId.ShouldBeNull();
        properties.ContentType.ShouldBeNull();
        properties.ContentEncoding.ShouldBeNull();
        properties.AbsoluteExpiryTime.ShouldBeNull();
        properties.CreationTime.ShouldBeNull();
        properties.GroupId.ShouldBeNull();
        properties.GroupSequence.ShouldBeNull();
        properties.ReplyToGroupId.ShouldBeNull();
    }
}
