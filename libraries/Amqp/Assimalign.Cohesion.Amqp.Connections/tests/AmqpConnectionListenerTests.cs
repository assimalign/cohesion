using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpConnectionListenerTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should reject a null SASL mechanism")]
    public void Create_WithNullMechanism_ShouldThrowArgumentException()
    {
        // Arrange
        AmqpConnectionListenerOptions options = new();
        options.SaslMechanisms.Add(null!);

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpConnectionListener.Create(new TestConnectionListener(), options));

        // Assert
        exception.ParamName.ShouldBe("options");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should reject two SASL mechanisms with the same name")]
    public void Create_WithDuplicateMechanisms_ShouldThrowArgumentException()
    {
        // Arrange
        AmqpConnectionListenerOptions options = new();
        options.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        options.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpConnectionListener.Create(new TestConnectionListener(), options));

        // Assert
        exception.Message.ShouldContain("ANONYMOUS", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should reject options whose open exceeds the 512 bytes allowed before the peer's open")]
    public void Create_WithOpenAbove512Bytes_ShouldThrowArgumentException()
    {
        // Arrange
        AmqpConnectionListenerOptions options = new()
        {
            Properties = new Dictionary<AmqpSymbol, object?> { ["padding"] = new string('x', 600) }
        };
        TestConnectionListener carrier = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpConnectionListener.Create(carrier, options));

        // Assert
        exception.ParamName.ShouldBe("options");
        exception.Message.ShouldStartWith("The local 'open' encodes to ");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should reject mechanisms whose sasl-mechanisms frame exceeds 512 bytes")]
    public void Create_WithMechanismsAbove512Bytes_ShouldThrowArgumentException()
    {
        // Arrange
        AmqpConnectionListenerOptions options = new();

        for (int index = 0; index < 3; index++)
        {
            options.SaslMechanisms.Add(new TestNamedSaslServerMechanism(string.Concat("X-", index.ToString(CultureInfo.InvariantCulture), new string('M', 200))));
        }

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpConnectionListener.Create(new TestConnectionListener(), options));

        // Assert
        exception.ParamName.ShouldBe("options");
        exception.Message.ShouldStartWith("The local 'sasl-mechanisms' encodes to a SASL frame of ");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Properties: Should represent the carrier listener's endpoint and capabilities")]
    public async Task Properties_OnListener_ShouldForwardCarrierListener()
    {
        // Arrange
        TestConnectionListener carrier = new();

        // Act
        await using AmqpConnectionListener listener = AmqpConnectionListener.Create(carrier, new AmqpConnectionListenerOptions { ContainerId = "broker" });

        // Assert
        listener.Id.ShouldBe(carrier.Id);
        listener.EndPoint.ShouldBe(carrier.EndPoint);
        listener.Capabilities.ShouldBe(carrier.Capabilities);
        listener.ContainerId.ShouldBe("broker");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should generate a container identifier when none is configured")]
    public async Task Create_WithoutContainerId_ShouldGenerateOne()
    {
        // Act
        await using AmqpConnectionListener first = AmqpConnectionListener.Create(new TestConnectionListener());
        await using AmqpConnectionListener second = AmqpConnectionListener.Create(new TestConnectionListener());

        // Assert
        first.ContainerId.ShouldStartWith("cohesion-");
        second.ContainerId.ShouldNotBe(first.ContainerId);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - BindAsync: Should bind the carrier listener")]
    public async Task BindAsync_OnNewListener_ShouldBindCarrierListener()
    {
        // Arrange
        TestConnectionListener carrier = new();
        await using AmqpConnectionListener listener = AmqpConnectionListener.Create(carrier);

        // Act
        await listener.BindAsync();

        // Assert
        carrier.BindCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - BindAsync: Should throw once the listener is disposed")]
    public async Task BindAsync_AfterDispose_ShouldThrowObjectDisposedException()
    {
        // Arrange
        AmqpConnectionListener listener = AmqpConnectionListener.Create(new TestConnectionListener());
        await listener.DisposeAsync();

        // Act and Assert
        Should.Throw<ObjectDisposedException>(() => listener.BindAsync());
        await Should.ThrowAsync<ObjectDisposedException>(async () => await listener.AcceptAsync());
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DisposeAsync: Should dispose the carrier listener once")]
    public async Task DisposeAsync_OnListener_ShouldDisposeCarrierListenerOnce()
    {
        // Arrange
        TestConnectionListener carrier = new();
        AmqpConnectionListener listener = AmqpConnectionListener.Create(carrier);

        // Act
        await listener.DisposeAsync();
        await listener.DisposeAsync();

        // Assert
        carrier.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DisposeAsync: Should release a connection still being established and keep accepted ones open")]
    public async Task DisposeAsync_WithPendingHandshake_ShouldReleasePendingAndKeepAccepted()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        TestConnection stalled = new();
        TestConnectionListener carrier = new();
        carrier.Enqueue(stalled);
        AmqpConnectionListener listener = AmqpConnectionListener.Create(carrier);
        AmqpTestPeer peer = new(stalled);
        Task<IAmqpConnection> pending = listener.AcceptAsync(timeout.Token).AsTask();

        // The listener answers the header only after it has taken the carrier off its queue and started
        // establishing it. Waiting for the answer leaves the connection in flight, waiting for an open
        // that never comes; without it, a busy thread pool could leave the carrier still queued.
        await peer.WriteProtocolHeaderAsync(AmqpFrame.AmqpProtocolId, timeout.Token);
        byte[] answer = await peer.ReadProtocolHeaderAsync(timeout.Token);
        answer.ShouldBe(AmqpFrame.AmqpProtocolHeader.ToArray());

        // Act
        await listener.DisposeAsync();
        await host.Listener.DisposeAsync();

        // Assert
        await stalled.WhenClosedAsync().WaitAsync(timeout.Token);
        await Should.ThrowAsync<Exception>(pending);
        host.Server.State.ShouldBe(ConnectionState.Open);
        await host.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should return connections in the order their establishment completes")]
    public async Task AcceptAsync_WithConcurrentHandshakes_ShouldReturnInCompletionOrder()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection slowCarrier = new();
        TestConnection fastCarrier = new();
        TestConnectionListener carrier = new();
        carrier.Enqueue(slowCarrier);
        carrier.Enqueue(fastCarrier);
        await using AmqpConnectionListener listener = AmqpConnectionListener.Create(carrier, new AmqpConnectionListenerOptions { MaxConcurrentHandshakes = 2 });
        await using AmqpTestPeer slow = new(slowCarrier);
        await using AmqpTestPeer fast = new(fastCarrier);
        Task<IAmqpConnection> first = listener.AcceptAsync(timeout.Token).AsTask();

        // Act
        await slow.WriteProtocolHeaderAsync(AmqpFrame.AmqpProtocolId, timeout.Token);
        await fast.OpenAsClientAsync(new AmqpOpenPerformative { ContainerId = "fast" }, timeout.Token);
        IAmqpConnection firstAccepted = await first;
        Task<IAmqpConnection> second = listener.AcceptAsync(timeout.Token).AsTask();
        await slow.WriteFrameAsync(0, new AmqpOpenPerformative { ContainerId = "slow" }, timeout.Token);
        IAmqpConnection secondAccepted = await second;

        // Assert
        firstAccepted.RemoteContainerId.ShouldBe("fast");
        secondAccepted.RemoteContainerId.ShouldBe("slow");
        listener.Connections.ShouldContain(firstAccepted);
        listener.Connections.ShouldContain(secondAccepted);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should carry each connection on the first stream its multiplexed peer opens")]
    public async Task AcceptAsync_OnMultiplexedCarrier_ShouldEstablishOnPeerStream()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (TestConnection clientStream, TestConnection serverStream) = TestConnection.CreatePair(TestMultiplexedConnection.DefaultCapabilities with { IsMultiplexed = false });
        TestMultiplexedConnection clientCarrier = new();
        TestMultiplexedConnection serverCarrier = new();
        clientCarrier.Enqueue(clientStream);
        serverCarrier.Enqueue(serverStream);
        TestMultiplexedConnectionListener carrierListener = new();
        TestMultiplexedConnectionFactory carrierFactory = new();
        carrierListener.Enqueue(serverCarrier);
        carrierFactory.Enqueue(clientCarrier);
        await using AmqpConnectionListener listener = AmqpConnectionListener.Create(carrierListener);
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(carrierFactory);

        // Act
        Task<IAmqpConnection> accept = listener.AcceptAsync(timeout.Token).AsTask();
        IAmqpConnection client = await factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token);
        IAmqpConnection server = await accept;
        await client.CloseAsync(cancellationToken: timeout.Token);

        // Assert
        serverCarrier.AcceptStreamCount.ShouldBe(1);
        clientCarrier.OpenStreamCount.ShouldBe(1);
        clientCarrier.LastOpenedDirection.ShouldBe(ConnectionDirection.Bidirectional);
        listener.Capabilities.IsMultiplexed.ShouldBeFalse();
        factory.Capabilities.IsMultiplexed.ShouldBeFalse();
        await clientCarrier.WhenClosedAsync().WaitAsync(timeout.Token);
        await server.DisposeAsync();
        await serverCarrier.WhenClosedAsync().WaitAsync(timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Features: Should expose the carrier's features and the SASL outcome")]
    public async Task Features_OnAcceptedConnection_ShouldExposeCarrierFeatures()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new();
        options.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        (TestConnection clientCarrier, TestConnection serverCarrier) = TestConnection.CreatePair();
        TestConnectionFeature feature = new("test.carrier");
        serverCarrier.Features.Set(feature);
        TestConnectionListener carrierListener = new();
        TestConnectionFactory carrierFactory = new();
        carrierListener.Enqueue(serverCarrier);
        carrierFactory.Enqueue(clientCarrier);
        await using AmqpConnectionListener listener = AmqpConnectionListener.Create(carrierListener, options);
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(carrierFactory, new AmqpConnectionFactoryOptions { SaslMechanism = new AmqpSaslAnonymousClientMechanism() });

        // Act
        Task<IAmqpConnection> accept = listener.AcceptAsync(timeout.Token).AsTask();
        await using IAmqpConnection client = await factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token);
        await using IAmqpConnection server = await accept;

        // Assert
        server.Features.Get<TestConnectionFeature>().ShouldBeSameAs(feature);
        server.Features.Get("test.carrier").ShouldBeSameAs(feature);
        server.Features.Get<IAmqpSaslConnectionInfo>().ShouldNotBeNull().Mechanism.ShouldBe((AmqpSymbol)"ANONYMOUS");
        server.Features.Get(IAmqpSaslConnectionInfo.FeatureName).ShouldBeAssignableTo<IAmqpSaslConnectionInfo>();
        server.RemoteEndPoint.ShouldBe(serverCarrier.RemoteEndPoint);
        server.LocalEndPoint.ShouldBe(serverCarrier.LocalEndPoint);
    }
}
