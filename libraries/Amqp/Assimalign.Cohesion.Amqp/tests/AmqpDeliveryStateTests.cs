using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpDeliveryStateTests
{
    public static TheoryData<AmqpDeliveryState, string> States => new()
    {
        { AmqpAccepted.Instance, "accepted" },
        { AmqpReleased.Instance, "released" },
        { new AmqpRejected(), "rejected" },
        { new AmqpRejected(new AmqpError(AmqpErrorCondition.NotFound, "no queue")), "rejected (amqp:not-found: no queue)" },
        { new AmqpModified(deliveryFailed: true), "modified" },
        { new AmqpReceived(2, 10), "received (2, 10)" },
        { new AmqpDeclared(new byte[] { 0x0a, 0x0b }), "declared (0a0b)" },
        { new AmqpTransactionalState(new byte[] { 0x0a, 0x0b }), "transactional-state (0a0b)" },
        { new AmqpTransactionalState(new byte[] { 0x0a, 0x0b }, AmqpAccepted.Instance), "transactional-state (0a0b, accepted)" }
    };

    [Theory(DisplayName = "Cohesion Test [Amqp] - ToString: Should name the state by its specification name with its fields")]
    [MemberData(nameof(States))]
    public void ToString_OnState_ShouldNameStateAndFields(AmqpDeliveryState state, string expected)
    {
        // Act
        string text = state.ToString()!;

        // Assert
        text.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Modified: Should default to a delivery that neither failed nor is undeliverable here")]
    public void Modified_WithDefaults_ShouldLeaveFlagsUnset()
    {
        // Arrange
        Dictionary<AmqpSymbol, object?> annotations = new() { [new AmqpSymbol("x-opt-retry")] = 1 };

        // Act
        AmqpModified defaults = new();
        AmqpModified modified = new(deliveryFailed: true, undeliverableHere: true, messageAnnotations: annotations);

        // Assert
        defaults.DeliveryFailed.ShouldBeFalse();
        defaults.UndeliverableHere.ShouldBeFalse();
        defaults.MessageAnnotations.ShouldBeNull();
        modified.DeliveryFailed.ShouldBeTrue();
        modified.UndeliverableHere.ShouldBeTrue();
        modified.MessageAnnotations.ShouldBeSameAs(annotations);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Outcome: Should make every outcome a delivery state and the received and transactional states not outcomes")]
    public void Outcome_OnStates_ShouldSeparateOutcomesFromOtherStates()
    {
        // Act
        AmqpDeliveryState received = new AmqpReceived(0, 0);
        AmqpDeliveryState transactional = new AmqpTransactionalState(new byte[] { 1 });

        // Assert
        received.ShouldNotBeAssignableTo<AmqpOutcome>();
        transactional.ShouldNotBeAssignableTo<AmqpOutcome>();
        AmqpAccepted.Instance.ShouldBeAssignableTo<AmqpDeliveryState>();
        new AmqpDeclared(new byte[] { 1 }).ShouldBeAssignableTo<AmqpOutcome>();
    }
}
