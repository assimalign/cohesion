using System;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpCarrierCapabilityTests
{
    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should reject a datagram carrier listener")]
    public void Create_OnDatagramCarrierListener_ShouldThrowArgumentException()
    {
        // Arrange
        TestConnectionListener listener = new(TestConnection.DefaultCapabilities with { Delivery = ConnectionDelivery.Datagram });

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpConnectionListener.Create(listener));

        // Assert
        exception.ParamName.ShouldBe("listener");
        listener.IsDisposed.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should reject an unreliable carrier listener")]
    public void Create_OnUnreliableCarrierListener_ShouldThrowArgumentException()
    {
        // Arrange
        TestConnectionListener listener = new(TestConnection.DefaultCapabilities with { IsReliable = false });

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpConnectionListener.Create(listener));

        // Assert
        exception.ParamName.ShouldBe("listener");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should reject an unordered multiplexed carrier listener")]
    public void Create_OnUnorderedMultiplexedCarrierListener_ShouldThrowArgumentException()
    {
        // Arrange
        TestMultiplexedConnectionListener listener = new(TestMultiplexedConnection.DefaultCapabilities with { IsOrdered = false });

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpConnectionListener.Create(listener));

        // Assert
        exception.ParamName.ShouldBe("listener");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should accept reliable, ordered stream carrier listeners of either kind")]
    public async Task Create_OnReliableOrderedStreamListeners_ShouldCreateListener()
    {
        // Arrange
        TestConnectionListener listener = new();
        TestMultiplexedConnectionListener multiplexedListener = new();

        // Act
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener);
        await using AmqpConnectionListener multiplexedAmqp = AmqpConnectionListener.Create(multiplexedListener);

        // Assert
        amqp.Connections.ShouldBeEmpty();
        multiplexedAmqp.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should reject a null carrier listener")]
    public void Create_OnNullListener_ShouldThrowArgumentNullException()
    {
        // Act + Assert
        Should.Throw<ArgumentNullException>(() => AmqpConnectionListener.Create((IDuplexConnectionListener)null!)).ParamName.ShouldBe("listener");
        Should.Throw<ArgumentNullException>(() => AmqpConnectionListener.Create((IMultiplexedConnectionListener)null!)).ParamName.ShouldBe("listener");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should reject a datagram carrier factory")]
    public void Create_OnDatagramCarrierFactory_ShouldThrowArgumentException()
    {
        // Arrange
        TestConnectionFactory factory = new(TestConnection.DefaultCapabilities with { Delivery = ConnectionDelivery.Datagram });

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpConnectionFactory.Create(factory));

        // Assert
        exception.ParamName.ShouldBe("factory");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should reject an unreliable multiplexed carrier factory")]
    public void Create_OnUnreliableMultiplexedCarrierFactory_ShouldThrowArgumentException()
    {
        // Arrange
        TestMultiplexedConnectionFactory factory = new(TestMultiplexedConnection.DefaultCapabilities with { IsReliable = false });

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpConnectionFactory.Create(factory));

        // Assert
        exception.ParamName.ShouldBe("factory");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should reject an unordered carrier factory")]
    public void Create_OnUnorderedCarrierFactory_ShouldThrowArgumentException()
    {
        // Arrange
        TestConnectionFactory factory = new(TestConnection.DefaultCapabilities with { IsOrdered = false });

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpConnectionFactory.Create(factory));

        // Assert
        exception.ParamName.ShouldBe("factory");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should accept reliable, ordered stream carrier factories of either kind")]
    public void Create_OnReliableOrderedStreamFactories_ShouldCreateFactory()
    {
        // Arrange
        TestConnectionFactory factory = new();
        TestMultiplexedConnectionFactory multiplexedFactory = new();

        // Act
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory);
        AmqpConnectionFactory multiplexedAmqp = AmqpConnectionFactory.Create(multiplexedFactory);

        // Assert
        amqp.Capabilities.IsReliable.ShouldBeTrue();
        multiplexedAmqp.Capabilities.IsOrdered.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should reject a null carrier factory")]
    public void Create_OnNullFactory_ShouldThrowArgumentNullException()
    {
        // Act + Assert
        Should.Throw<ArgumentNullException>(() => AmqpConnectionFactory.Create((IDuplexConnectionFactory)null!)).ParamName.ShouldBe("factory");
        Should.Throw<ArgumentNullException>(() => AmqpConnectionFactory.Create((IMultiplexedConnectionFactory)null!)).ParamName.ShouldBe("factory");
    }
}
