using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpErrorTests
{
    [Fact(DisplayName = "Cohesion Test [Amqp] - ToString: Should print the condition, then the description when there is one")]
    public void ToString_WithAndWithoutDescription_ShouldPrintCondition()
    {
        // Arrange
        AmqpError bare = new(AmqpErrorCondition.ResourceLocked);
        AmqpError described = new(AmqpErrorCondition.NotAllowed, "queue is exclusive");

        // Act
        string bareText = bare.ToString();
        string describedText = described.ToString();

        // Assert
        bareText.ShouldBe("amqp:resource-locked");
        describedText.ShouldBe("amqp:not-allowed: queue is exclusive");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Constructor: Should keep the condition, description and info")]
    public void Constructor_WithInfo_ShouldKeepFields()
    {
        // Arrange
        Dictionary<AmqpSymbol, object?> info = new() { [new AmqpSymbol("hostname")] = "broker-2" };

        // Act
        AmqpError error = new(AmqpErrorCondition.ConnectionRedirect, "moved", info);

        // Assert
        error.Condition.ShouldBe(AmqpErrorCondition.ConnectionRedirect);
        error.Description.ShouldBe("moved");
        error.Info.ShouldBeSameAs(info);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Exception: Should default to the Other error code with no AMQP error")]
    public void Exception_WithMessageOnly_ShouldUseOtherCode()
    {
        // Act
        AmqpException exception = new("failed");

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.Other);
        exception.Error.ShouldBeNull();
        exception.Message.ShouldBe("failed");
        exception.InnerException.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Exception: Should keep the error code, inner exception and AMQP error")]
    public void Exception_WithCodeAndError_ShouldKeepThem()
    {
        // Arrange
        InvalidOperationException inner = new("inner");
        AmqpError error = new(AmqpErrorCondition.DetachForced);

        // Act
        AmqpException exception = new(AmqpErrorCode.LinkDetached, "detached", inner) { Error = error };

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        exception.InnerException.ShouldBeSameAs(inner);
        exception.Error.ShouldBeSameAs(error);
        new AmqpException("wrapped", inner).ErrorCode.ShouldBe(AmqpErrorCode.Other);
    }
}
