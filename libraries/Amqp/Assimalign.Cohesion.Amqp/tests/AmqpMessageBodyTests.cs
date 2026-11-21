using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpMessageBodyTests
{
    [Fact(DisplayName = "Cohesion Test [Amqp] - DataBody: Should hold one data section, which may be empty")]
    public void DataBody_WithOneSection_ShouldHoldSection()
    {
        // Act
        AmqpDataBody body = new(new byte[] { 1, 2 });
        AmqpDataBody empty = new(ReadOnlyMemory<byte>.Empty);

        // Assert
        body.Sections.Count.ShouldBe(1);
        body.Sections[0].ToArray().ShouldBe(new byte[] { 1, 2 });
        empty.Sections.Count.ShouldBe(1);
        empty.Sections[0].IsEmpty.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - DataBody: Should refuse a null or empty list of sections")]
    public void DataBody_WithNoSections_ShouldThrow()
    {
        // Act
        ArgumentException empty = Should.Throw<ArgumentException>(() => new AmqpDataBody(Array.Empty<ReadOnlyMemory<byte>>()));
        ArgumentNullException missing = Should.Throw<ArgumentNullException>(() => new AmqpDataBody((IReadOnlyList<ReadOnlyMemory<byte>>)null!));

        // Assert
        empty.ParamName.ShouldBe("sections");
        empty.Message.ShouldStartWith(ErrorMessages.BodySectionsEmpty);
        missing.ParamName.ShouldBe("sections");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - SequenceBody: Should hold its sequences in order")]
    public void SequenceBody_WithSequences_ShouldHoldThemInOrder()
    {
        // Arrange
        IReadOnlyList<object?>[] sequences = [new object?[] { 1, "a" }, new object?[] { null }];

        // Act
        AmqpSequenceBody body = new(sequences);

        // Assert
        body.Sequences.Count.ShouldBe(2);
        body.Sequences[0].ShouldBe(new object?[] { 1, "a" });
        body.Sequences[1].ShouldBe(new object?[] { null });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - SequenceBody: Should refuse a null or empty list, or a null sequence in it")]
    public void SequenceBody_WithMissingSequences_ShouldThrow()
    {
        // Act
        ArgumentException empty = Should.Throw<ArgumentException>(() => new AmqpSequenceBody(Array.Empty<IReadOnlyList<object?>>()));
        ArgumentNullException missing = Should.Throw<ArgumentNullException>(() => new AmqpSequenceBody(null!));
        ArgumentNullException nullSequence = Should.Throw<ArgumentNullException>(() => new AmqpSequenceBody(new IReadOnlyList<object?>[] { null! }));

        // Assert
        empty.ParamName.ShouldBe("sequences");
        empty.Message.ShouldStartWith(ErrorMessages.BodySectionsEmpty);
        missing.ParamName.ShouldBe("sequences");
        nullSequence.ParamName.ShouldBe("sequences");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - ValueBody: Should hold any value, null included")]
    public void ValueBody_WithNull_ShouldHoldNull()
    {
        // Act
        AmqpValueBody body = new(null);

        // Assert
        body.Value.ShouldBeNull();
        new AmqpValueBody("payload").Value.ShouldBe("payload");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Header: Should default to the values the specification gives an absent header")]
    public void Header_OnConstruction_ShouldUseSpecificationDefaults()
    {
        // Act
        AmqpHeader header = new();

        // Assert
        header.Durable.ShouldBeFalse();
        header.Priority.ShouldBe((byte)4);
        header.TimeToLive.ShouldBeNull();
        header.FirstAcquirer.ShouldBeFalse();
        header.DeliveryCount.ShouldBe(0u);
    }
}
