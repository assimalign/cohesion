using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpConnectionOptionsTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(5);

    private static readonly EndPoint _testEndPoint = new IPEndPoint(IPAddress.Loopback, 5672);

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Constructor: Should use the documented listener defaults")]
    public void Constructor_OnListenerOptions_ShouldUseDocumentedDefaults()
    {
        // Act
        AmqpConnectionListenerOptions options = new();

        // Assert
        options.InitialProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        options.AutoNegotiateProtocolHeader.ShouldBeTrue();
        options.MaxFrameSize.ShouldBe(262_144u);
        options.HandshakeTimeout.ShouldBe(TimeSpan.FromSeconds(10));
        options.MaxConcurrentHandshakes.ShouldBe(512);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Constructor: Should use the documented factory defaults")]
    public void Constructor_OnFactoryOptions_ShouldUseDocumentedDefaults()
    {
        // Act
        AmqpConnectionFactoryOptions options = new();

        // Assert
        options.InitialProtocolHeader.ShouldBe(AmqpProtocolHeader.Amqp10);
        options.AutoNegotiateProtocolHeader.ShouldBeTrue();
        options.MaxFrameSize.ShouldBe(262_144u);
        options.HandshakeTimeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - MaxFrameSize: Should reject a size below the AMQP minimum of 512 bytes")]
    [InlineData(0u)]
    [InlineData(511u)]
    public void MaxFrameSize_BelowProtocolMinimum_ShouldThrowArgumentOutOfRangeException(uint maxFrameSize)
    {
        // Arrange
        AmqpConnectionListenerOptions listenerOptions = new();
        AmqpConnectionFactoryOptions factoryOptions = new();

        // Act + Assert
        Should.Throw<ArgumentOutOfRangeException>(() => listenerOptions.MaxFrameSize = maxFrameSize);
        Should.Throw<ArgumentOutOfRangeException>(() => factoryOptions.MaxFrameSize = maxFrameSize);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - MaxFrameSize: Should accept the AMQP minimum of 512 bytes")]
    public void MaxFrameSize_AtProtocolMinimum_ShouldBeAccepted()
    {
        // Arrange
        AmqpConnectionListenerOptions listenerOptions = new();
        AmqpConnectionFactoryOptions factoryOptions = new();

        // Act
        listenerOptions.MaxFrameSize = 512u;
        factoryOptions.MaxFrameSize = 512u;

        // Assert
        listenerOptions.MaxFrameSize.ShouldBe(512u);
        factoryOptions.MaxFrameSize.ShouldBe(512u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - HandshakeTimeout: Should reject a timeout longer than a timer supports")]
    public void HandshakeTimeout_AboveTimerMaximum_ShouldThrowArgumentOutOfRangeException()
    {
        // Arrange
        AmqpConnectionListenerOptions listenerOptions = new();
        AmqpConnectionFactoryOptions factoryOptions = new();
        TimeSpan tooLong = TimeSpan.FromMilliseconds(uint.MaxValue);

        // Act + Assert
        Should.Throw<ArgumentOutOfRangeException>(() => listenerOptions.HandshakeTimeout = tooLong);
        Should.Throw<ArgumentOutOfRangeException>(() => factoryOptions.HandshakeTimeout = tooLong);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - HandshakeTimeout: Should accept a non-positive timeout, which disables the limit")]
    [InlineData(0)]
    [InlineData(-1)]
    public void HandshakeTimeout_OnNonPositiveValue_ShouldBeAccepted(int milliseconds)
    {
        // Arrange
        AmqpConnectionListenerOptions listenerOptions = new();
        AmqpConnectionFactoryOptions factoryOptions = new();
        TimeSpan disabled = TimeSpan.FromMilliseconds(milliseconds);

        // Act
        listenerOptions.HandshakeTimeout = disabled;
        factoryOptions.HandshakeTimeout = disabled;

        // Assert
        listenerOptions.HandshakeTimeout.ShouldBe(disabled);
        factoryOptions.HandshakeTimeout.ShouldBe(disabled);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - MaxConcurrentHandshakes: Should reject a limit below one")]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxConcurrentHandshakes_BelowOne_ShouldThrowArgumentOutOfRangeException(int maxConcurrentHandshakes)
    {
        // Arrange
        AmqpConnectionListenerOptions options = new();

        // Act + Assert
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxConcurrentHandshakes = maxConcurrentHandshakes);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should keep the listener options read at creation")]
    public async Task Create_OnListenerOptionsChangedAfterCreation_ShouldKeepOptionsReadAtCreation()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        TestConnectionListener listener = new();
        listener.Enqueue(carrier);
        AmqpConnectionListenerOptions options = new() { AutoNegotiateProtocolHeader = false };
        await using AmqpConnectionListener amqp = AmqpConnectionListener.Create(listener, options);
        options.AutoNegotiateProtocolHeader = true;

        // Act
        await using IAmqpConnection connection = await amqp.AcceptAsync(timeout.Token);

        // Assert
        connection.RemoteProtocolHeader.ShouldBeNull();
        carrier.HasBufferedPeerBytes.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should keep the factory options read at creation")]
    public async Task Create_OnFactoryOptionsChangedAfterCreation_ShouldKeepOptionsReadAtCreation()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection carrier = new();
        TestConnectionFactory factory = new();
        factory.Enqueue(carrier);
        AmqpConnectionFactoryOptions options = new() { AutoNegotiateProtocolHeader = false };
        AmqpConnectionFactory amqp = AmqpConnectionFactory.Create(factory, options);
        options.AutoNegotiateProtocolHeader = true;

        // Act
        await using IAmqpConnection connection = await amqp.ConnectAsync(_testEndPoint, timeout.Token);

        // Assert
        connection.RemoteProtocolHeader.ShouldBeNull();
        carrier.HasBufferedPeerBytes.ShouldBeFalse();
    }
}
