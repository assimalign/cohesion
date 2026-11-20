using System;
using System.Globalization;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpConnectionOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ListenerOptions: Should start from the documented defaults")]
    public void ListenerOptions_OnNew_ShouldHaveDefaults()
    {
        // Act
        AmqpConnectionListenerOptions options = new();

        // Assert
        options.ContainerId.ShouldBeNull();
        options.MaxFrameSize.ShouldBe(262_144u);
        options.ChannelMax.ShouldBe((ushort)255);
        options.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(1));
        options.HandshakeTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        options.MaxConcurrentHandshakes.ShouldBe(512);
        options.SaslMechanisms.ShouldBeEmpty();
        options.SessionOptions.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - FactoryOptions: Should start from the documented defaults")]
    public void FactoryOptions_OnNew_ShouldHaveDefaults()
    {
        // Act
        AmqpConnectionFactoryOptions options = new();

        // Assert
        options.ContainerId.ShouldBeNull();
        options.HostName.ShouldBeNull();
        options.MaxFrameSize.ShouldBe(262_144u);
        options.ChannelMax.ShouldBe((ushort)255);
        options.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(1));
        options.HandshakeTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        options.SaslMechanism.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ListenerOptions: Should refuse limits AMQP 1.0 does not allow")]
    public void ListenerOptions_OnInvalidValues_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        AmqpConnectionListenerOptions options = new();

        // Act and Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxFrameSize = 511);
        Should.Throw<ArgumentOutOfRangeException>(() => options.IdleTimeout = TimeSpan.FromMilliseconds(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => options.IdleTimeout = TimeSpan.FromDays(60));
        Should.Throw<ArgumentOutOfRangeException>(() => options.HandshakeTimeout = TimeSpan.FromDays(60));
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxConcurrentHandshakes = 0);
        options.MaxFrameSize = 512;
        options.MaxFrameSize.ShouldBe(512u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - FactoryOptions: Should refuse limits AMQP 1.0 does not allow")]
    public void FactoryOptions_OnInvalidValues_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        AmqpConnectionFactoryOptions options = new();

        // Act and Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxFrameSize = 100);
        Should.Throw<ArgumentOutOfRangeException>(() => options.IdleTimeout = TimeSpan.FromSeconds(-1));
        Should.Throw<ArgumentOutOfRangeException>(() => options.HandshakeTimeout = TimeSpan.FromDays(60));
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - ListenerOptions: Should refuse an idle timeout above zero and under 200 ms")]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(199)]
    public void ListenerOptions_OnIdleTimeoutTooShort_ShouldThrowArgumentOutOfRangeException(int milliseconds)
    {
        // Arrange
        AmqpConnectionListenerOptions options = new();

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(() => options.IdleTimeout = TimeSpan.FromMilliseconds(milliseconds));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldStartWith(string.Format(CultureInfo.CurrentCulture, ErrorMessages.IdleTimeoutTooShort, TimeSpan.FromMilliseconds(200)));
        options.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - FactoryOptions: Should refuse an idle timeout above zero and under 200 ms")]
    [InlineData(1)]
    [InlineData(199)]
    public void FactoryOptions_OnIdleTimeoutTooShort_ShouldThrowArgumentOutOfRangeException(int milliseconds)
    {
        // Arrange
        AmqpConnectionFactoryOptions options = new();

        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(() => options.IdleTimeout = TimeSpan.FromMilliseconds(milliseconds));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldStartWith(string.Format(CultureInfo.CurrentCulture, ErrorMessages.IdleTimeoutTooShort, TimeSpan.FromMilliseconds(200)));
        options.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Options: Should take an idle timeout of zero or from 200 ms up")]
    public void Options_OnIdleTimeoutZeroOrFrom200Milliseconds_ShouldTakeIt()
    {
        // Arrange
        AmqpConnectionListenerOptions listenerOptions = new();
        AmqpConnectionFactoryOptions factoryOptions = new();

        // Act
        listenerOptions.IdleTimeout = TimeSpan.Zero;
        factoryOptions.IdleTimeout = TimeSpan.FromMilliseconds(200);

        // Assert
        listenerOptions.IdleTimeout.ShouldBe(TimeSpan.Zero);
        factoryOptions.IdleTimeout.ShouldBe(TimeSpan.FromMilliseconds(200));
        listenerOptions.IdleTimeout = TimeSpan.FromMilliseconds(200);
        factoryOptions.IdleTimeout = TimeSpan.Zero;
        listenerOptions.IdleTimeout.ShouldBe(TimeSpan.FromMilliseconds(200));
        factoryOptions.IdleTimeout.ShouldBe(TimeSpan.Zero);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should read the options once, so later changes do not apply")]
    public async Task Create_WithOptionsChangedAfterwards_ShouldKeepCreationValues()
    {
        // Arrange
        AmqpConnectionListenerOptions listenerOptions = new() { ContainerId = "listener" };
        AmqpConnectionFactoryOptions factoryOptions = new() { ContainerId = "factory" };

        // Act
        await using AmqpConnectionListener listener = AmqpConnectionListener.Create(new TestConnectionListener(), listenerOptions);
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(new TestConnectionFactory(), factoryOptions);
        listenerOptions.ContainerId = "changed";
        listenerOptions.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        factoryOptions.ContainerId = "changed";

        // Assert
        listener.ContainerId.ShouldBe("listener");
        factory.ContainerId.ShouldBe("factory");
    }
}
