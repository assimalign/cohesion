using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Covers establishment (AMQP 1.0 Part 2, section 2.4; Part 5, section 5.3): protocol header negotiation,
/// the SASL exchange, the <c>open</c> exchange, and how failures reach the factory's caller and the
/// listener.
/// </summary>
public class AmqpHandshakeTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should authenticate with PLAIN and expose the identity on both ends")]
    public async Task ConnectAsync_WithSaslPlain_ShouldAuthenticateBothEnds()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpSaslPlainCredentials? received = null;
        AmqpConnectionListenerOptions listenerOptions = new();
        listenerOptions.SaslMechanisms.Add(AmqpSaslPlainServerMechanism.Create((credentials, _) =>
        {
            received = credentials;
            return ValueTask.FromResult(credentials.UserName == "alice" && credentials.Password == "secret");
        }));
        AmqpConnectionFactoryOptions factoryOptions = new() { SaslMechanism = AmqpSaslPlainClientMechanism.Create("alice", "secret") };

        // Act
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(listenerOptions, factoryOptions, timeout.Token);

        // Assert
        received.ShouldNotBeNull().UserName.ShouldBe("alice");
        IAmqpSaslConnectionInfo server = host.Server.AmqpSasl.ShouldNotBeNull();
        server.Mechanism.ShouldBe((AmqpSymbol)"PLAIN");
        server.Identity.ShouldBe("alice");
        server.HostName.ShouldBe("amqp.test");
        host.Client.AmqpSasl.ShouldNotBeNull().Mechanism.ShouldBe((AmqpSymbol)"PLAIN");
        host.Server.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should authenticate with ANONYMOUS")]
    public async Task ConnectAsync_WithSaslAnonymous_ShouldOpenBothEnds()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions listenerOptions = new();
        listenerOptions.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        AmqpConnectionFactoryOptions factoryOptions = new() { SaslMechanism = new AmqpSaslAnonymousClientMechanism { Trace = "test" } };

        // Act
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(listenerOptions, factoryOptions, timeout.Token);

        // Assert
        host.Server.AmqpSasl.ShouldNotBeNull().Mechanism.ShouldBe((AmqpSymbol)"ANONYMOUS");
        host.Client.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should run the challenge and response loop and pass the outcome's additional data")]
    public async Task ConnectAsync_WithChallengeMechanism_ShouldCompleteChallengeLoop()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions listenerOptions = new();
        listenerOptions.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        listenerOptions.SaslMechanisms.Add(new TestChallengeSaslServerMechanism());
        TestChallengeSaslClientMechanism client = new();

        // Act
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(listenerOptions, new AmqpConnectionFactoryOptions { SaslMechanism = client }, timeout.Token);

        // Assert
        host.Server.AmqpSasl.ShouldNotBeNull().Identity.ShouldBe(TestChallengeSaslServerMechanism.Identity);
        client.CompletedWith.ShouldBe(TestChallengeSaslServerMechanism.Welcome);
        Encoding.ASCII.GetString(host.Client.AmqpSasl.ShouldNotBeNull().AdditionalData.Span).ShouldBe(TestChallengeSaslServerMechanism.Welcome);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should fail authentication when the server rejects the credentials")]
    public async Task ConnectAsync_WithWrongPassword_ShouldThrowAuthenticationFailed()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions listenerOptions = new();
        listenerOptions.SaslMechanisms.Add(AmqpSaslPlainServerMechanism.Create((credentials, _) => ValueTask.FromResult(credentials.Password == "secret")));
        await using CarrierPair pair = CarrierPair.Create(listenerOptions, new AmqpConnectionFactoryOptions { SaslMechanism = AmqpSaslPlainClientMechanism.Create("alice", "guess") });
        _ = pair.Listener.AcceptAsync(timeout.Token).AsTask();

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(pair.Factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask());

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.AuthenticationFailed);
        pair.ClientCarrier.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should fail authentication when the challenge is answered wrongly")]
    public async Task ConnectAsync_WithWrongChallengeAnswer_ShouldThrowAuthenticationFailed()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions listenerOptions = new();
        listenerOptions.SaslMechanisms.Add(new TestChallengeSaslServerMechanism());
        await using CarrierPair pair = CarrierPair.Create(listenerOptions, new AmqpConnectionFactoryOptions { SaslMechanism = new TestChallengeSaslClientMechanism { WrongAnswer = true } });
        _ = pair.Listener.AcceptAsync(timeout.Token).AsTask();

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(pair.Factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask());

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.AuthenticationFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should fail authentication when the server does not offer the mechanism")]
    public async Task ConnectAsync_WithMechanismNotOffered_ShouldThrowAuthenticationFailed()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions listenerOptions = new();
        listenerOptions.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        await using CarrierPair pair = CarrierPair.Create(listenerOptions, new AmqpConnectionFactoryOptions { SaslMechanism = AmqpSaslPlainClientMechanism.Create("alice", "secret") });
        _ = pair.Listener.AcceptAsync(timeout.Token).AsTask();

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(pair.Factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask());

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.AuthenticationFailed);
        exception.Message.ShouldContain("PLAIN", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should fail authentication when the listener requires SASL and the client skips it")]
    public async Task ConnectAsync_WithoutSaslToSaslListener_ShouldThrowAuthenticationFailed()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions listenerOptions = new();
        listenerOptions.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        await using CarrierPair pair = CarrierPair.Create(listenerOptions, null);
        _ = pair.Listener.AcceptAsync(timeout.Token).AsTask();

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(pair.Factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask());

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.AuthenticationFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should fail authentication when the client uses SASL and the listener offers none")]
    public async Task ConnectAsync_WithSaslToPlainListener_ShouldThrowAuthenticationFailed()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using CarrierPair pair = CarrierPair.Create(null, new AmqpConnectionFactoryOptions { SaslMechanism = new AmqpSaslAnonymousClientMechanism() });
        _ = pair.Listener.AcceptAsync(timeout.Token).AsTask();

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(pair.Factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask());

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.AuthenticationFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should receive a system outcome when the server mechanism throws")]
    public async Task ConnectAsync_OnThrowingServerMechanism_ShouldThrowAuthenticationFailed()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions listenerOptions = new();
        listenerOptions.SaslMechanisms.Add(new TestThrowingSaslServerMechanism());
        await using CarrierPair pair = CarrierPair.Create(listenerOptions, new AmqpConnectionFactoryOptions { SaslMechanism = new AmqpSaslAnonymousClientMechanism() });
        _ = pair.Listener.AcceptAsync(timeout.Token).AsTask();

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(pair.Factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token).AsTask());

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.AuthenticationFailed);
        exception.Message.ShouldContain(nameof(AmqpSaslCode.Sys), Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should send PLAIN's initial response and the host name in sasl-init")]
    public async Task ConnectAsync_WithSaslPlain_ShouldSendInitialResponseAndHostName()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(
            new AmqpConnectionFactoryOptions { SaslMechanism = AmqpSaslPlainClientMechanism.Create("alice", "secret") },
            timeout.Token);

        // Act
        byte[] saslHeader = await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.WriteProtocolHeaderAsync(AmqpFrame.SaslProtocolId, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpSaslMechanismsPerformative { Mechanisms = ["ANONYMOUS", "PLAIN"] }, timeout.Token, type: AmqpFrame.SaslType);
        AmqpTestFrame initFrame = await peer.ReadNonEmptyFrameAsync(timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpSaslOutcomePerformative { Code = AmqpSaslCode.Ok }, timeout.Token, type: AmqpFrame.SaslType);
        await peer.OpenAsServerAsync(null, timeout.Token);
        await using IAmqpConnection connection = await connect;
        await using AmqpTestPeer hangUp = peer;

        // Assert
        saslHeader.ShouldBe(AmqpFrame.SaslProtocolHeader.ToArray());
        initFrame.Header.Type.ShouldBe(AmqpFrame.SaslType);
        AmqpSaslInitPerformative init = initFrame.Performative.ShouldBeOfType<AmqpSaslInitPerformative>();
        init.Mechanism.ShouldBe((AmqpSymbol)"PLAIN");
        init.HostName.ShouldBe("amqp.test");
        init.InitialResponse.ShouldNotBeNull().ToArray().ShouldBe(Encoding.UTF8.GetBytes("\0alice\0secret"));
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should name the endpoint's host and the configured limits in open")]
    public async Task ConnectAsync_OnScriptedServer_ShouldSendConfiguredOpen()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionFactoryOptions options = new()
        {
            ContainerId = "client-container",
            MaxFrameSize = 8192,
            ChannelMax = 31,
            IdleTimeout = TimeSpan.FromSeconds(20)
        };
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(options, timeout.Token);

        // Act
        AmqpOpenPerformative open = await peer.OpenAsServerAsync(AmqpTestPeer.CreateOpen(maxFrameSize: 4096, channelMax: 7), timeout.Token);
        await using IAmqpConnection connection = await connect;
        await using AmqpTestPeer hangUp = peer;

        // Assert
        open.ContainerId.ShouldBe("client-container");
        open.HostName.ShouldBe("amqp.test");
        open.MaxFrameSize.ShouldBe(8192u);
        open.ChannelMax.ShouldBe((ushort)31);
        open.IdleTimeout.ShouldBe(10_000u);
        connection.RemoteContainerId.ShouldBe("scripted-peer");
        connection.MaxFrameSize.ShouldBe(4096u);
        connection.ChannelMax.ShouldBe((ushort)7);
        factory.ContainerId.ShouldBe("client-container");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should name the configured host instead of the endpoint's")]
    public async Task ConnectAsync_WithHostNameOption_ShouldOverrideEndPointHost()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) =
            AmqpTestPeer.StartFactory(new AmqpConnectionFactoryOptions { HostName = "virtual-host" }, timeout.Token);

        // Act
        AmqpOpenPerformative open = await peer.OpenAsServerAsync(null, timeout.Token);
        await using IAmqpConnection connection = await connect;
        await using AmqpTestPeer hangUp = peer;

        // Assert
        open.HostName.ShouldBe("virtual-host");
        open.ContainerId.ShouldStartWith("cohesion-");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should skip empty frames before the peer's open")]
    public async Task ConnectAsync_OnEmptyFramesBeforeOpen_ShouldSkipThem()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(cancellationToken: timeout.Token);
        byte[] emptyFrame = [0x00, 0x00, 0x00, 0x08, 0x02, 0x00, 0x00, 0x00];

        // Act
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.ReadPerformativeAsync<AmqpOpenPerformative>(timeout.Token);
        await peer.WriteProtocolHeaderAsync(AmqpFrame.AmqpProtocolId, timeout.Token);
        await peer.WriteAsync(emptyFrame, timeout.Token);
        await peer.WriteAsync(emptyFrame, timeout.Token);
        await peer.WriteFrameAsync(0, AmqpTestPeer.CreateOpen(), timeout.Token);
        await using IAmqpConnection connection = await connect;
        await using AmqpTestPeer hangUp = peer;

        // Assert
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should close and fail when the peer's first frame is not open")]
    public async Task ConnectAsync_OnFirstFrameNotOpen_ShouldCloseAndThrowProtocolViolation()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(cancellationToken: timeout.Token);

        // Act
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.ReadPerformativeAsync<AmqpOpenPerformative>(timeout.Token);
        await peer.WriteProtocolHeaderAsync(AmqpFrame.AmqpProtocolId, timeout.Token);
        await peer.WriteFrameAsync(0, AmqpTestPeer.CreateBegin(), timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(connect);
        AmqpError? close = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
        close.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
        peer.Carrier.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should close with a framing error when open arrives on another channel")]
    public async Task ConnectAsync_OnOpenOnNonZeroChannel_ShouldCloseWithFramingError()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(cancellationToken: timeout.Token);

        // Act
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        await peer.ReadPerformativeAsync<AmqpOpenPerformative>(timeout.Token);
        await peer.WriteProtocolHeaderAsync(AmqpFrame.AmqpProtocolId, timeout.Token);
        await peer.WriteFrameAsync(3, AmqpTestPeer.CreateOpen(), timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(connect);
        AmqpError? close = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.FramingError);
        close.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.FramingError);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should fail with a closed connection when the peer hangs up during establishment")]
    public async Task ConnectAsync_OnPeerHangingUp_ShouldThrowConnectionClosed()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(cancellationToken: timeout.Token);

        // Act
        await peer.ReadProtocolHeaderAsync(timeout.Token);
        peer.Carrier.CompletePeerOutput();
        AmqpException exception = await Should.ThrowAsync<AmqpException>(connect);

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.ConnectionClosed);
        peer.Carrier.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should time out and release the carrier when the peer never answers")]
    public async Task ConnectAsync_OnSilentPeer_ShouldThrowTimeoutException()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) =
            AmqpTestPeer.StartFactory(new AmqpConnectionFactoryOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(200) }, timeout.Token);

        // Act
        await Should.ThrowAsync<TimeoutException>(connect);

        // Assert
        peer.Carrier.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should cancel and release the carrier when the caller cancels")]
    public async Task ConnectAsync_OnCanceledToken_ShouldThrowOperationCanceledException()
    {
        // Arrange
        using CancellationTokenSource cancellation = new();
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(cancellationToken: cancellation.Token);

        // Act
        await peer.ReadProtocolHeaderAsync(CancellationToken.None);
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(connect);

        // Assert
        peer.Carrier.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ConnectAsync: Should refuse an open larger than 512 bytes before the peer's limits are known")]
    public async Task ConnectAsync_WithOpenAbove512Bytes_ShouldThrowAndReleaseCarrier()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        Dictionary<AmqpSymbol, object?> properties = new() { ["padding"] = new string('x', 600) };
        (AmqpTestPeer peer, AmqpConnectionFactory factory, Task<IAmqpConnection> connect) =
            AmqpTestPeer.StartFactory(new AmqpConnectionFactoryOptions { Properties = properties }, timeout.Token);

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(connect);

        // Assert
        exception.Message.ShouldContain("512");
        peer.Carrier.IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should answer an invalid protocol header with the AMQP header and hang up")]
    public async Task AcceptAsync_OnInvalidProtocolHeader_ShouldAnswerAmqpHeaderAndHangUp()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionListener listener, Task<IAmqpConnection> accept) = AmqpTestPeer.StartListener(cancellationToken: timeout.Token);
        await using AmqpConnectionListener owned = listener;

        // Act
        await peer.WriteAsync("GET / HT"u8.ToArray(), timeout.Token);
        byte[] answer = await peer.ReadProtocolHeaderAsync(timeout.Token);
        IReadOnlyList<AmqpPerformative> rest = await peer.ReadToEndAsync(timeout.Token);

        // Assert
        answer.ShouldBe(AmqpFrame.AmqpProtocolHeader.ToArray());
        rest.ShouldBeEmpty();
        accept.IsCompleted.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should answer an unsupported header with the supported one and hang up")]
    [InlineData((byte)2, (byte)1, (byte)0, (byte)0)]
    [InlineData((byte)0, (byte)1, (byte)0, (byte)1)]
    [InlineData((byte)0, (byte)2, (byte)0, (byte)0)]
    public async Task AcceptAsync_OnUnsupportedHeader_ShouldAnswerSupportedHeader(byte protocolId, byte major, byte minor, byte revision)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpTestPeer peer, AmqpConnectionListener listener, Task<IAmqpConnection> accept) = AmqpTestPeer.StartListener(cancellationToken: timeout.Token);
        await using AmqpConnectionListener owned = listener;

        // Act
        await peer.WriteAsync(new byte[] { (byte)'A', (byte)'M', (byte)'Q', (byte)'P', protocolId, major, minor, revision }, timeout.Token);
        byte[] answer = await peer.ReadProtocolHeaderAsync(timeout.Token);
        IReadOnlyList<AmqpPerformative> rest = await peer.ReadToEndAsync(timeout.Token);

        // Assert
        answer.ShouldBe(AmqpFrame.AmqpProtocolHeader.ToArray());
        rest.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should answer the SASL header when SASL is required and the client skips it")]
    public async Task AcceptAsync_OnAmqpHeaderWhenSaslRequired_ShouldAnswerSaslHeader()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new();
        options.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
        (AmqpTestPeer peer, AmqpConnectionListener listener, Task<IAmqpConnection> accept) = AmqpTestPeer.StartListener(options, timeout.Token);
        await using AmqpConnectionListener owned = listener;

        // Act
        await peer.WriteProtocolHeaderAsync(AmqpFrame.AmqpProtocolId, timeout.Token);
        byte[] answer = await peer.ReadProtocolHeaderAsync(timeout.Token);
        IReadOnlyList<AmqpPerformative> rest = await peer.ReadToEndAsync(timeout.Token);

        // Assert
        answer.ShouldBe(AmqpFrame.SaslProtocolHeader.ToArray());
        rest.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should keep accepting after a connection fails establishment")]
    public async Task AcceptAsync_AfterFailedEstablishment_ShouldReturnNextConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        TestConnection broken = new();
        (TestConnection clientCarrier, TestConnection serverCarrier) = TestConnection.CreatePair();
        TestConnectionListener carrierListener = new();
        TestConnectionFactory carrierFactory = new();
        carrierListener.Enqueue(broken);
        carrierListener.Enqueue(serverCarrier);
        carrierFactory.Enqueue(clientCarrier);
        await using AmqpConnectionListener listener = AmqpConnectionListener.Create(carrierListener);
        AmqpConnectionFactory factory = AmqpConnectionFactory.Create(carrierFactory);

        // Act
        Task<IAmqpConnection> accept = listener.AcceptAsync(timeout.Token).AsTask();
        await broken.WritePeerAsync("NOT AMQP"u8.ToArray(), timeout.Token);
        await using IAmqpConnection client = await factory.ConnectAsync(AmqpTestHost.EndPoint, timeout.Token);
        await using IAmqpConnection server = await accept;

        // Assert
        server.RemoteContainerId.ShouldBe(client.ContainerId);
        await broken.WhenClosedAsync().WaitAsync(timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should accept a client that pipelines its header and open")]
    public async Task AcceptAsync_OnPipelinedHeaderAndOpen_ShouldOpen()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(
            new AmqpConnectionListenerOptions { ContainerId = "server-container", IdleTimeout = TimeSpan.Zero },
            AmqpTestPeer.CreateOpen(maxFrameSize: 2048, channelMax: 3),
            timeout.Token);

        // Assert
        host.EngineOpen.ContainerId.ShouldBe("server-container");
        host.EngineOpen.IdleTimeout.ShouldBeNull();
        host.Connection.RemoteContainerId.ShouldBe("scripted-peer");
        host.Connection.MaxFrameSize.ShouldBe(2048u);
        host.Connection.ChannelMax.ShouldBe((ushort)3);
        host.Listener.ContainerId.ShouldBe("server-container");
    }

    /// <summary>
    /// A listener and a factory over one in-memory carrier pair, for tests in which establishment fails, so
    /// the listener never returns the connection.
    /// </summary>
    private sealed class CarrierPair : IAsyncDisposable
    {
        private CarrierPair(AmqpConnectionListener listener, AmqpConnectionFactory factory, TestConnection clientCarrier, TestConnection serverCarrier)
        {
            Listener = listener;
            Factory = factory;
            ClientCarrier = clientCarrier;
            ServerCarrier = serverCarrier;
        }

        public AmqpConnectionListener Listener { get; }

        public AmqpConnectionFactory Factory { get; }

        public TestConnection ClientCarrier { get; }

        public TestConnection ServerCarrier { get; }

        public static CarrierPair Create(AmqpConnectionListenerOptions? listenerOptions, AmqpConnectionFactoryOptions? factoryOptions)
        {
            (TestConnection clientCarrier, TestConnection serverCarrier) = TestConnection.CreatePair();
            TestConnectionListener carrierListener = new();
            TestConnectionFactory carrierFactory = new();

            carrierListener.Enqueue(serverCarrier);
            carrierFactory.Enqueue(clientCarrier);

            return new CarrierPair(
                AmqpConnectionListener.Create(carrierListener, listenerOptions),
                AmqpConnectionFactory.Create(carrierFactory, factoryOptions),
                clientCarrier,
                serverCarrier);
        }

        public ValueTask DisposeAsync()
        {
            return Listener.DisposeAsync();
        }
    }
}
