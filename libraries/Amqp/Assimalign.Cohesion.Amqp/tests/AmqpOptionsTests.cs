using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Amqp] - SessionOptions: Should default to a 2048-transfer window and 1024 handles")]
    public void SessionOptions_OnConstruction_ShouldUseDefaults()
    {
        // Act
        AmqpSessionOptions options = new();

        // Assert
        options.IncomingWindow.ShouldBe(2048u);
        options.HandleMax.ShouldBe(1023u);
        options.OfferedCapabilities.ShouldBeNull();
        options.DesiredCapabilities.ShouldBeNull();
        options.Properties.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - IncomingWindow: Should refuse a zero window, which would stall every transfer")]
    public void IncomingWindow_WithZero_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        AmqpSessionOptions options = new();

        // Act
        Should.Throw<ArgumentOutOfRangeException>(() => options.IncomingWindow = 0);

        // Assert
        options.IncomingWindow.ShouldBe(2048u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - SenderOptions: Should default to mixed and first settlement, auto-completed drains and no size limit")]
    public void SenderOptions_OnConstruction_ShouldUseDefaults()
    {
        // Act
        AmqpSenderOptions options = new();

        // Assert
        options.SenderSettleMode.ShouldBe(AmqpSenderSettleMode.Mixed);
        options.ReceiverSettleMode.ShouldBe(AmqpReceiverSettleMode.First);
        options.AutoCompleteDrain.ShouldBeTrue();
        options.MaxMessageSize.ShouldBeNull();
        options.Name.ShouldBeNull();
        options.Unsettled.ShouldBeNull();
        options.IncompleteUnsettled.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - ReceiverOptions: Should default to a prefetch of 100 messages and a 16 MiB size limit")]
    public void ReceiverOptions_OnConstruction_ShouldUseDefaults()
    {
        // Act
        AmqpReceiverOptions options = new();

        // Assert
        options.Prefetch.ShouldBe(100u);
        options.MaxMessageSize.ShouldBe(16ul * 1024 * 1024);
        options.SenderSettleMode.ShouldBe(AmqpSenderSettleMode.Mixed);
        options.ReceiverSettleMode.ShouldBe(AmqpReceiverSettleMode.First);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - MaxMessageSize: Should refuse zero, which AMQP reserves for no limit by omission")]
    public void MaxMessageSize_WithZero_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        AmqpReceiverOptions options = new() { MaxMessageSize = 1024 };

        // Act
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxMessageSize = 0);
        options.MaxMessageSize = null;

        // Assert
        options.MaxMessageSize.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - SettleMode: Should refuse a settlement mode the specification does not define")]
    public void SettleMode_WithUndefinedValue_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        AmqpSenderOptions options = new();

        // Act
        ArgumentOutOfRangeException sender = Should.Throw<ArgumentOutOfRangeException>(() => options.SenderSettleMode = (AmqpSenderSettleMode)3);
        ArgumentOutOfRangeException receiver = Should.Throw<ArgumentOutOfRangeException>(() => options.ReceiverSettleMode = (AmqpReceiverSettleMode)2);
        options.SenderSettleMode = AmqpSenderSettleMode.Settled;
        options.ReceiverSettleMode = AmqpReceiverSettleMode.Second;

        // Assert
        sender.ParamName.ShouldBe("value");
        receiver.ParamName.ShouldBe("value");
        options.SenderSettleMode.ShouldBe(AmqpSenderSettleMode.Settled);
        options.ReceiverSettleMode.ShouldBe(AmqpReceiverSettleMode.Second);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - DeliveryOptions: Should default to an engine-chosen tag, message format 0 and the link's settlement")]
    public void DeliveryOptions_OnConstruction_ShouldUseDefaults()
    {
        // Act
        AmqpDeliveryOptions options = new();

        // Assert
        options.DeliveryTag.ShouldBeNull();
        options.MessageFormat.ShouldBe(0u);
        options.Settled.ShouldBeNull();
        options.State.ShouldBeNull();
        options.Resume.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Terminus: Should default to no durability, session-end expiry and no timeout")]
    public void Terminus_OnConstruction_ShouldUseSpecificationDefaults()
    {
        // Act
        AmqpSource source = new();
        AmqpTarget target = new();

        // Assert
        source.Durable.ShouldBe(AmqpTerminusDurability.None);
        source.ExpiryPolicy.ShouldBe(AmqpTerminusExpiryPolicy.SessionEnd);
        source.Timeout.ShouldBe(0u);
        source.Dynamic.ShouldBeFalse();
        target.Durable.ShouldBe(AmqpTerminusDurability.None);
        target.ExpiryPolicy.ShouldBe(AmqpTerminusExpiryPolicy.SessionEnd);
        target.Timeout.ShouldBe(0u);
        target.Dynamic.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Durable: Should refuse a terminus durability the specification does not define")]
    public void Durable_WithUndefinedValue_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        AmqpSource source = new();
        AmqpTarget target = new();

        // Act
        ArgumentOutOfRangeException sourceException = Should.Throw<ArgumentOutOfRangeException>(() => source.Durable = (AmqpTerminusDurability)3);
        ArgumentOutOfRangeException targetException = Should.Throw<ArgumentOutOfRangeException>(() => target.Durable = (AmqpTerminusDurability)3);
        source.Durable = AmqpTerminusDurability.UnsettledState;
        target.Durable = AmqpTerminusDurability.Configuration;

        // Assert
        sourceException.ParamName.ShouldBe("value");
        targetException.ParamName.ShouldBe("value");
        source.Durable.ShouldBe(AmqpTerminusDurability.UnsettledState);
        target.Durable.ShouldBe(AmqpTerminusDurability.Configuration);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - ExpiryPolicy: Should refuse an expiry policy the specification does not define")]
    [InlineData("later")]
    [InlineData("SESSION-END")]
    [InlineData(null)]
    public void ExpiryPolicy_WithUndefinedPolicy_ShouldThrowArgumentException(string? policy)
    {
        // Arrange
        AmqpSymbol symbol = policy is null ? default : new AmqpSymbol(policy);
        AmqpSource source = new();
        AmqpTarget target = new();

        // Act
        ArgumentException sourceException = Should.Throw<ArgumentException>(() => source.ExpiryPolicy = symbol);
        ArgumentException targetException = Should.Throw<ArgumentException>(() => target.ExpiryPolicy = symbol);

        // Assert
        sourceException.ParamName.ShouldBe("value");
        targetException.ParamName.ShouldBe("value");
        source.ExpiryPolicy.ShouldBe(AmqpTerminusExpiryPolicy.SessionEnd);
        target.ExpiryPolicy.ShouldBe(AmqpTerminusExpiryPolicy.SessionEnd);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - ExpiryPolicy: Should accept each of the four policies the specification defines")]
    [InlineData("link-detach")]
    [InlineData("session-end")]
    [InlineData("connection-close")]
    [InlineData("never")]
    public void ExpiryPolicy_WithDefinedPolicy_ShouldKeepIt(string policy)
    {
        // Arrange
        AmqpSymbol symbol = new(policy);
        AmqpSource source = new();
        AmqpTarget target = new();

        // Act
        source.ExpiryPolicy = symbol;
        target.ExpiryPolicy = symbol;

        // Assert
        source.ExpiryPolicy.ShouldBe(symbol);
        target.ExpiryPolicy.ShouldBe(symbol);
    }
}
