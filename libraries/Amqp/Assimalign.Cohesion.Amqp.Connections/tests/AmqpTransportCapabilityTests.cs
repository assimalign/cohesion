using System;
using System.Net;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpTransportCapabilityTests
{
    private static readonly EndPoint _testEndPoint = new IPEndPoint(IPAddress.Loopback, 5672);

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Constructor: Should reject a datagram carrier listener")]
    public async Task Constructor_OnDatagramCarrierListener_ShouldThrowArgumentException()
    {
        // Arrange
        await using TestConnectionListener listener = new(
            TestConnection.DefaultCapabilities with { Delivery = ConnectionDelivery.Datagram });

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new AmqpServerTransport(listener));

        // Assert
        exception.ParamName.ShouldBe("listener");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Constructor: Should reject an unreliable carrier listener")]
    public async Task Constructor_OnUnreliableCarrierListener_ShouldThrowArgumentException()
    {
        // Arrange
        await using TestConnectionListener listener = new(
            TestConnection.DefaultCapabilities with { IsReliable = false });

        // Act + Assert
        Should.Throw<ArgumentException>(() => new AmqpServerTransport(listener));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Constructor: Should reject an unordered multiplexed carrier listener")]
    public async Task Constructor_OnUnorderedMultiplexedCarrierListener_ShouldThrowArgumentException()
    {
        // Arrange
        await using TestMultiplexedConnectionListener listener = new(
            TestMultiplexedConnection.DefaultCapabilities with { IsOrdered = false });

        // Act + Assert
        Should.Throw<ArgumentException>(() => new AmqpServerTransport(listener));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Constructor: Should accept reliable ordered stream carrier listeners")]
    public async Task Constructor_OnReliableOrderedStreamListeners_ShouldConstructServerTransport()
    {
        // Arrange
        await using TestConnectionListener listener = new();
        await using TestMultiplexedConnectionListener multiplexedListener = new();

        // Act
        await using AmqpServerTransport singleStreamTransport = new(listener);
        await using AmqpServerTransport multiplexedTransport = new(multiplexedListener);

        // Assert
        singleStreamTransport.Connections.ShouldBeEmpty();
        multiplexedTransport.Connections.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Constructor: Should reject a null carrier listener")]
    public void Constructor_OnNullListener_ShouldThrowArgumentNullException()
    {
        // Act + Assert
        Should.Throw<ArgumentNullException>(() => new AmqpServerTransport((IConnectionListener)null!));
        Should.Throw<ArgumentNullException>(() => new AmqpServerTransport((IMultiplexedConnectionListener)null!));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Constructor: Should reject a datagram carrier factory")]
    public void Constructor_OnDatagramCarrierFactory_ShouldThrowArgumentException()
    {
        // Arrange
        TestConnectionFactory factory = new(
            TestConnection.DefaultCapabilities with { Delivery = ConnectionDelivery.Datagram });

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => new AmqpClientTransport(factory, _testEndPoint));

        // Assert
        exception.ParamName.ShouldBe("factory");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Constructor: Should reject an unreliable multiplexed carrier factory")]
    public void Constructor_OnUnreliableMultiplexedCarrierFactory_ShouldThrowArgumentException()
    {
        // Arrange
        TestMultiplexedConnectionFactory factory = new(
            TestMultiplexedConnection.DefaultCapabilities with { IsReliable = false });

        // Act + Assert
        Should.Throw<ArgumentException>(() => new AmqpClientTransport(factory, _testEndPoint));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Constructor: Should reject an unordered carrier factory")]
    public void Constructor_OnUnorderedCarrierFactory_ShouldThrowArgumentException()
    {
        // Arrange
        TestConnectionFactory factory = new(
            TestConnection.DefaultCapabilities with { IsOrdered = false });

        // Act + Assert
        Should.Throw<ArgumentException>(() => new AmqpClientTransport(factory, _testEndPoint));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Constructor: Should accept reliable ordered stream carrier factories")]
    public async Task Constructor_OnReliableOrderedStreamFactories_ShouldConstructClientTransport()
    {
        // Arrange
        TestConnectionFactory factory = new();
        TestMultiplexedConnectionFactory multiplexedFactory = new();

        // Act
        await using AmqpClientTransport singleStreamTransport = new(factory, _testEndPoint);
        await using AmqpClientTransport multiplexedTransport = new(multiplexedFactory, _testEndPoint);

        // Assert
        singleStreamTransport.EndPoint.ShouldBeSameAs(_testEndPoint);
        multiplexedTransport.EndPoint.ShouldBeSameAs(_testEndPoint);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Constructor: Should reject null carrier factory arguments")]
    public void Constructor_OnNullFactoryArguments_ShouldThrowArgumentNullException()
    {
        // Arrange
        TestConnectionFactory factory = new();

        // Act + Assert
        Should.Throw<ArgumentNullException>(() => new AmqpClientTransport((IConnectionFactory)null!, _testEndPoint));
        Should.Throw<ArgumentNullException>(() => new AmqpClientTransport((IMultiplexedConnectionFactory)null!, _testEndPoint));
        Should.Throw<ArgumentNullException>(() => new AmqpClientTransport(factory, null!));
    }
}
