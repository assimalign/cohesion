using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Drives an accepted connection with a scripted peer that breaks the protocol, and checks that the engine
/// answers each violation at the level AMQP 1.0 assigns it: the connection, the session or the link.
/// </summary>
public class AmqpEngineViolationTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ProcessFrame: Should close the connection when a frame uses a channel above channel-max")]
    public async Task ProcessFrame_OnChannelAboveChannelMax_ShouldCloseWithFramingError()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(new AmqpConnectionListenerOptions { ChannelMax = 3 }, null, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;
        AmqpOpenPerformative open = host.EngineOpen;

        // Act
        await peer.WriteFrameAsync(9, AmqpTestPeer.CreateBegin(), timeout.Token);
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        open.ChannelMax.ShouldBe((ushort)3);
        error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.FramingError);
        await peer.WriteFrameAsync(0, new AmqpClosePerformative(), timeout.Token);
        await WaitForClosedAsync(connection, timeout.Token);
        connection.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ProcessFrame: Should close the connection when a SASL frame follows open")]
    public async Task ProcessFrame_OnSaslFrameAfterOpen_ShouldCloseWithFramingError()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;

        // Act
        await peer.WriteFrameAsync(0, new AmqpSaslOutcomePerformative { Code = AmqpSaslCode.Ok }, timeout.Token, type: AmqpFrame.SaslType);
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.FramingError);
        await peer.WriteFrameAsync(0, new AmqpClosePerformative(), timeout.Token);
        await WaitForClosedAsync(connection, timeout.Token);
        connection.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ProcessFrame: Should close the connection when the peer sends open twice")]
    public async Task ProcessFrame_OnSecondOpen_ShouldCloseWithIllegalState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;

        // Act
        await peer.WriteFrameAsync(0, AmqpTestPeer.CreateOpen(), timeout.Token);
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ProcessFrame: Should close the connection when a frame names a channel no session uses")]
    public async Task ProcessFrame_OnUnmappedChannel_ShouldCloseWithIllegalState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;

        // Act
        await peer.WriteFrameAsync(2, new AmqpEndPerformative(), timeout.Token);
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ProcessFrame: Should close the connection when a performative is unknown")]
    public async Task ProcessFrame_OnUnknownPerformative_ShouldCloseWithNotImplemented()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;

        // A frame whose body is a list described by the unassigned descriptor 0x7f.
        byte[] frame = [0x00, 0x00, 0x00, 0x0C, 0x02, 0x00, 0x00, 0x00, 0x00, 0x53, 0x7F, 0x45];

        // Act
        await peer.WriteAsync(frame, timeout.Token);
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotImplemented);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ProcessFrame: Should close the connection when a frame exceeds the local max-frame-size")]
    public async Task ProcessFrame_OnFrameAboveMaxFrameSize_ShouldCloseWithFramingError()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(new AmqpConnectionListenerOptions { MaxFrameSize = 512 }, null, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;
        await peer.BeginAsync(null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpFlowPerformative { IncomingWindow = 10, NextOutgoingId = 1, OutgoingWindow = 10 }, timeout.Token, payload: new byte[600]);
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.FramingError);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should close the connection when a handle exceeds handle-max")]
    public async Task OnAttach_OnHandleAboveHandleMax_ShouldCloseWithFramingError()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new() { SessionOptions = new AmqpSessionOptions { HandleMax = 3 } };
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(options, null, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;
        AmqpBeginPerformative begin = await peer.BeginAsync(null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateSenderAttach("link", handle: 9), timeout.Token);
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        begin.HandleMax.ShouldBe(3u);
        error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.FramingError);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should end the session when a handle is already in use")]
    public async Task OnAttach_OnHandleInUse_ShouldEndSessionWithHandleInUse()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;
        await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await connection.AcceptSessionAsync(timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateSenderAttach("first", handle: 0), timeout.Token);
        await peer.WriteFrameAsync(0, CreateSenderAttach("second", handle: 0), timeout.Token);
        AmqpEndPerformative end = await ReadUntilAsync<AmqpEndPerformative>(peer, timeout.Token);

        // Assert
        end.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.HandleInUse);
        connection.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
        session.SessionEnded.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should end the session when a transfer names an unattached handle")]
    public async Task OnTransfer_OnUnattachedHandle_ShouldEndSessionWithUnattachedHandle()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;
        await peer.BeginAsync(null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(handle: 5, deliveryId: 0), timeout.Token, payload: new byte[] { 0x00, 0x53, 0x77, 0x40 });
        AmqpEndPerformative end = await ReadUntilAsync<AmqpEndPerformative>(peer, timeout.Token);

        // Assert
        end.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.UnattachedHandle);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnFlow: Should end the session when a flow names an unattached handle")]
    public async Task OnFlow_OnUnattachedHandle_ShouldEndSessionWithUnattachedHandle()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;
        await peer.BeginAsync(null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpFlowPerformative
        {
            NextIncomingId = 1,
            IncomingWindow = 10,
            NextOutgoingId = 1,
            OutgoingWindow = 10,
            Handle = 4,
            DeliveryCount = 0,
            LinkCredit = 10
        }, timeout.Token);
        AmqpEndPerformative end = await ReadUntilAsync<AmqpEndPerformative>(peer, timeout.Token);

        // Assert
        end.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.UnattachedHandle);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDetach: Should end the session when a detach names an unattached handle")]
    public async Task OnDetach_OnUnattachedHandle_ShouldEndSessionWithUnattachedHandle()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;
        await peer.BeginAsync(null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 6, Closed = true }, timeout.Token);
        AmqpEndPerformative end = await ReadUntilAsync<AmqpEndPerformative>(peer, timeout.Token);

        // Assert
        end.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.UnattachedHandle);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDisposition: Should end the session when last precedes first")]
    public async Task OnDisposition_OnLastBeforeFirst_ShouldEndSessionWithInvalidField()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;
        await peer.BeginAsync(null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative { Role = AmqpRole.Receiver, First = 5, Last = 2, Settled = true }, timeout.Token);
        AmqpEndPerformative end = await ReadUntilAsync<AmqpEndPerformative>(peer, timeout.Token);

        // Assert
        end.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach the link when a transfer arrives without credit")]
    public async Task OnTransfer_WithoutCredit_ShouldDetachWithTransferLimitExceeded()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;
        await peer.BeginAsync(null, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, connection, new AmqpReceiverOptions { Prefetch = 0 }, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(handle: 0, deliveryId: 0), timeout.Token, payload: new byte[] { 0x00, 0x53, 0x77, 0x40 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Closed.ShouldBeTrue();
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.TransferLimitExceeded);
        receiver.LinkDetached.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach the link when a message exceeds max-message-size")]
    public async Task OnTransfer_AboveMaxMessageSize_ShouldDetachWithMessageSizeExceeded()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;
        await peer.BeginAsync(null, timeout.Token);
        await AttachPeerSenderAsync(peer, connection, new AmqpReceiverOptions { Prefetch = 10, MaxMessageSize = 16 }, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(handle: 0, deliveryId: 0), timeout.Token, payload: new byte[64]);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.MessageSizeExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnIdleTimer: Should close the connection when the peer stays silent past the idle timeout")]
    public async Task OnIdleTimer_OnSilentPeer_ShouldCloseWithResourceLimitExceeded()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new() { IdleTimeout = TimeSpan.FromMilliseconds(300) };
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(options, null, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;
        AmqpOpenPerformative open = host.EngineOpen;

        // Act
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        open.IdleTimeout.ShouldBe(150u);
        error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.ResourceLimitExceeded);
        await peer.WriteFrameAsync(0, new AmqpClosePerformative(), timeout.Token);
        await WaitForClosedAsync(connection, timeout.Token);
        connection.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnIdleTimer: Should send empty frames at half the peer's idle timeout")]
    public async Task OnIdleTimer_OnPeerIdleTimeout_ShouldSendEmptyFrames()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpOpenPerformative open = AmqpTestPeer.CreateOpen();
        open.IdleTimeout = 200;
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(null, open, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;

        // Act
        AmqpTestFrame? first = await peer.ReadFrameAsync(timeout.Token);
        AmqpTestFrame? second = await peer.ReadFrameAsync(timeout.Token);

        // Assert
        first.ShouldNotBeNull().Performative.ShouldBeNull();
        second.ShouldNotBeNull().Performative.ShouldBeNull();
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnClose: Should answer the peer's close and close the connection with its error")]
    public async Task OnClose_FromPeer_ShouldAnswerAndCloseWithPeerError()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;
        Task<IAmqpSession> acceptSession = connection.AcceptSessionAsync(timeout.Token).AsTask();

        // Act
        await peer.WriteFrameAsync(0, new AmqpClosePerformative { Error = new AmqpError(AmqpErrorCondition.ConnectionForced, "shutting down") }, timeout.Token);
        AmqpError? answer = await peer.ReadCloseAsync(timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(acceptSession);

        // Assert
        answer.ShouldBeNull();
        await WaitForClosedAsync(connection, timeout.Token);
        connection.State.ShouldBe(ConnectionState.Closed);
        exception.ErrorCode.ShouldBe(AmqpErrorCode.ConnectionClosed);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.ConnectionForced);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ProcessFrame: Should abort the connection when the carrier ends without close")]
    public async Task ProcessFrame_OnCarrierEnded_ShouldAbortConnection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpConnection connection = host.Connection;

        // Act
        peer.Carrier.CompletePeerOutput();
        await WaitForClosedAsync(connection, timeout.Token);

        // Assert
        connection.State.ShouldBe(ConnectionState.Aborted);
    }

    private static AmqpAttachPerformative CreateSenderAttach(string name, uint handle)
    {
        return new AmqpAttachPerformative
        {
            Name = name,
            Handle = handle,
            Role = AmqpRole.Sender,
            Source = new AmqpSource { Address = "peer" },
            Target = new AmqpTarget { Address = "queue" },
            InitialDeliveryCount = 0
        };
    }

    private static AmqpTransferPerformative CreateTransfer(uint handle, uint deliveryId)
    {
        return new AmqpTransferPerformative
        {
            Handle = handle,
            DeliveryId = deliveryId,
            DeliveryTag = AmqpDeliveryTag.Create(deliveryId),
            MessageFormat = 0,
            Settled = false
        };
    }

    private static async Task<IAmqpReceiver> AttachPeerSenderAsync(
        AmqpTestPeer peer,
        IAmqpConnection connection,
        AmqpReceiverOptions options,
        CancellationToken cancellationToken)
    {
        IAmqpSession session = await connection.AcceptSessionAsync(cancellationToken);

        await peer.WriteFrameAsync(0, CreateSenderAttach("link", handle: 0), cancellationToken);

        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);
        IAmqpReceiver receiver = await request.AcceptReceiverAsync(options, cancellationToken);
        AmqpAttachPerformative answer = await ReadUntilAsync<AmqpAttachPerformative>(peer, cancellationToken);

        answer.Role.ShouldBe(AmqpRole.Receiver);
        return receiver;
    }

    private static async Task<T> ReadUntilAsync<T>(AmqpTestPeer peer, CancellationToken cancellationToken)
        where T : AmqpPerformative
    {
        List<string> skipped = [];

        while (true)
        {
            AmqpTestFrame frame = await peer.ReadNonEmptyFrameAsync(cancellationToken);

            if (frame.Performative is T performative)
            {
                return performative;
            }

            frame.Performative.ShouldNotBeOfType<AmqpClosePerformative>($"The engine closed the connection after {string.Join(", ", skipped)}.");
            skipped.Add(frame.Performative!.PerformativeName);
        }
    }

    private static async Task WaitForClosedAsync(IAmqpConnection connection, CancellationToken cancellationToken)
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using (connection.ConnectionClosed.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), closed))
        {
            await closed.Task.WaitAsync(cancellationToken);
        }
    }
}
