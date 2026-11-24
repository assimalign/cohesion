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

using static Assimalign.Cohesion.Amqp.Connections.Tests.AmqpPeerScript;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Drives the engine's sessions with a scripted peer through the rules of AMQP 1.0 Part 2 for a session: beginning
/// it on a channel, the handles its links take, its incoming window, and ending it from either end, both, or after
/// an error. Each rule in <see cref="Rules"/> names the tests that pin it.
/// </summary>
public class AmqpSessionComplianceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    private static readonly byte[] _payload = [0x00, 0x53, 0x77, 0x40];

    /// <summary>
    /// Gets the Part 2 session rules the engine holds, each with the tests that pin it.
    /// </summary>
    internal static IReadOnlyList<AmqpComplianceRule> Rules { get; } =
    [
        new("S1", "A session begins with a begin from each end: the answer names the channel the peer began on as its remote-channel and takes the lowest channel the engine has free, and a begin whose remote-channel names no begin the engine sent closes the connection with amqp:illegal-state.", "Part 2, sections 2.5.1 and 2.7.2",
            Tests:
            [
                nameof(AmqpConnectionTests.BeginSessionAsync_OnOpenConnection_ShouldBeginSessionOnBothEnds),
                nameof(OnEnd_AfterSessionError_ShouldDiscardFramesUntilThePeerEnds)
            ],
            Refusals: [nameof(OnBegin_OnUnsolicitedRemoteChannel_ShouldCloseWithIllegalState)]),
        new("S2", "A channel carries one session at a time: a begin on a channel that already carries one closes the connection with amqp:illegal-state, and the channel is free again once both ends have ended its session.", "Part 2, sections 2.5.1 and 2.5.2",
            Tests:
            [
                nameof(EndAsync_OnPeerEndCrossingIt_ShouldTakeItAsTheAnswer),
                nameof(OnEnd_AfterSessionError_ShouldDiscardFramesUntilThePeerEnds)
            ],
            Refusals: [nameof(OnBegin_OnChannelInUse_ShouldCloseWithIllegalState)]),
        new("S3", "A frame on a channel that carries no session closes the connection with amqp:illegal-state.", "Part 2, section 2.5.5",
            Tests: [],
            Refusals: [nameof(AmqpEngineViolationTests.ProcessFrame_OnUnmappedChannel_ShouldCloseWithIllegalState)]),
        new("S4", "The engine answers a peer's begin on a channel no higher than the peer's channel-max: a begin it has no channel left to answer on closes the connection with amqp:resource-limit-exceeded.", "Part 2, sections 2.5.1 and 2.7.1",
            Tests: [],
            Refusals: [nameof(OnBegin_BeyondPeerChannelMax_ShouldCloseWithResourceLimitExceeded)]),
        new("S5", "A peer attaches no handle above the handle-max the engine declared in its begin: a higher handle closes the connection with amqp:connection:framing-error, and handle-max itself is accepted.", "Part 2, section 2.7.2",
            Tests: [nameof(OnAttach_OnHandleOfHandleMax_ShouldAttach)],
            Refusals: [nameof(AmqpEngineViolationTests.OnAttach_OnHandleAboveHandleMax_ShouldCloseWithFramingError)]),
        new("S6", "An attach on a handle the peer already uses ends the session with amqp:session:handle-in-use; a handle is free again once both ends have detached its link.", "Part 2, sections 2.6.2 and 2.8.17",
            Tests: [nameof(AmqpLinkComplianceTests.DetachAsync_OnAttachedLink_ShouldSuspendItWithoutClosing)],
            Refusals: [nameof(AmqpEngineViolationTests.OnAttach_OnHandleInUse_ShouldEndSessionWithHandleInUse)]),
        new("S7", "A transfer, flow or detach that names a handle no attached link uses ends the session with amqp:session:unattached-handle.", "Part 2, section 2.8.17",
            Tests: [],
            Refusals:
            [
                nameof(AmqpEngineViolationTests.OnTransfer_OnUnattachedHandle_ShouldEndSessionWithUnattachedHandle),
                nameof(AmqpEngineViolationTests.OnFlow_OnUnattachedHandle_ShouldEndSessionWithUnattachedHandle),
                nameof(AmqpEngineViolationTests.OnDetach_OnUnattachedHandle_ShouldEndSessionWithUnattachedHandle)
            ]),
        new("S8", "Each transfer takes a place in the receiving session's incoming window: the engine sends no transfer while the peer's window is closed and waits for a flow that opens it.", "Part 2, section 2.5.6",
            Tests: [nameof(SendAsync_OnClosedRemoteIncomingWindow_ShouldWaitForFlow)],
            Refusals: []),
        new("S9", "A session ends with an end from each end: the engine answers a peer's end, and an end it sends completes when the peer's end arrives.", "Part 2, sections 2.5.2 and 2.7.8",
            Tests: [nameof(AmqpConnectionTests.EndAsync_OnSession_ShouldEndPeerSessionAndDetachLinks)],
            Refusals: []),
        new("S10", "When both ends end a session at once, each takes the other's end as the answer to its own: the engine writes no second end, and the channel is free again.", "Part 2, section 2.5.3",
            Tests: [nameof(EndAsync_OnPeerEndCrossingIt_ShouldTakeItAsTheAnswer)],
            Refusals: []),
        new("S11", "A session that ends with an error discards every frame the peer sends on it until the peer's end, then frees its channel.", "Part 2, section 2.5.4",
            Tests: [nameof(OnEnd_AfterSessionError_ShouldDiscardFramesUntilThePeerEnds)],
            Refusals: []),
        new("S12", "The engine's incoming window bounds what its session holds: a frame keeps its place until the application takes the delivery it belongs to or that delivery's link stops, a frame the session keeps nothing of frees its place at once, and the frames of a delivery still being reassembled hold none, so a delivery larger than the window completes. The engine restores the window with a flow once half of it is free, never shrinks what it granted, and ends the session with amqp:session:window-violation when the peer sends past it.", "Part 2, sections 2.5.6 and 2.8.17",
            Tests:
            [
                nameof(ReceiveAsync_OnHalfIncomingWindowTaken_ShouldRestoreItWithFlow),
                nameof(ReceiveAsync_OnSlowConsumer_ShouldKeepIncomingWindowClosedUntilItTakesDeliveries),
                nameof(OnTransfer_OnDeliveryLargerThanIncomingWindow_ShouldCompleteIt),
                nameof(DetachAsync_OnReceiverHoldingIncomingWindow_ShouldGiveItsPlacesBack)
            ],
            Refusals: [nameof(OnTransfer_PastIncomingWindow_ShouldEndSessionWithWindowViolation)])
    ];

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnBegin: Should close the connection when a begin answers a begin the engine never sent")]
    public async Task OnBegin_OnUnsolicitedRemoteChannel_ShouldCloseWithIllegalState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;

        // Act: the begin names channel 4 as the one it answers, and the engine began no session there.
        await peer.WriteFrameAsync(0, AmqpTestPeer.CreateBegin(remoteChannel: 4), timeout.Token);
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
        error.Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.BeginUnsolicited, 4));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnBegin: Should close the connection when a begin uses a channel that already carries a session")]
    public async Task OnBegin_OnChannelInUse_ShouldCloseWithIllegalState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        await peer.BeginAsync(null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, AmqpTestPeer.CreateBegin(), timeout.Token);
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
        error.Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ChannelInUse, 0));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnBegin: Should close the connection when no channel within the peer's channel-max is left to answer a begin on")]
    public async Task OnBegin_BeyondPeerChannelMax_ShouldCloseWithResourceLimitExceeded()
    {
        // Arrange: the peer accepts channel 0 alone, and the first session takes it.
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(null, AmqpTestPeer.CreateOpen(channelMax: 0), timeout.Token);
        AmqpTestPeer peer = host.Peer;
        await peer.BeginAsync(null, timeout.Token);

        // Act: a channel the engine accepts, which leaves it no channel of its own to answer on.
        await peer.WriteFrameAsync(1, AmqpTestPeer.CreateBegin(), timeout.Token);
        AmqpError? error = await peer.ReadCloseAsync(timeout.Token);

        // Assert
        host.Connection.ChannelMax.ShouldBe((ushort)0);
        error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.ResourceLimitExceeded);
        error.Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ChannelsExhausted, 0));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should attach a link on the handle-max the engine declared")]
    public async Task OnAttach_OnHandleOfHandleMax_ShouldAttach()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new() { SessionOptions = new AmqpSessionOptions { HandleMax = 3 } };
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(options, null, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        AmqpAttachPerformative attach = CreateSenderAttach(new AmqpTarget { Address = "queue" });
        AmqpTransferPerformative transfer = CreateTransfer(deliveryId: 0, settled: true);
        attach.Handle = 3;
        transfer.Handle = 3;

        // Act
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, attach, null, timeout.Token);
        await peer.WriteFrameAsync(0, transfer, timeout.Token, payload: _payload);
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.Payload.ToArray().ShouldBe(_payload);
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
        host.Connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should hold a transfer while the peer's incoming window is closed and send it once a flow opens it")]
    public async Task SendAsync_OnClosedRemoteIncomingWindow_ShouldWaitForFlow()
    {
        // Arrange: the peer's window takes one transfer, and the first delivery uses it.
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(AmqpTestPeer.CreateBegin(incomingWindow: 1), timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, new AmqpSenderOptions { SenderSettleMode = AmqpSenderSettleMode.Settled }, timeout.Token);
        await peer.WriteFrameAsync(0, CreateLinkFlow(engineBegin, credit: 10, incomingWindow: 1), timeout.Token);
        await sender.WaitForCreditAsync(timeout.Token);
        await sender.SendAsync(new byte[] { 1 }, cancellationToken: timeout.Token);
        AmqpTransferPerformative first = await ReadUntilAsync<AmqpTransferPerformative>(peer, timeout.Token);
        uint seen = unchecked(engineBegin.NextOutgoingId + 1);

        // Act: a flow that has seen the first transfer keeps the window closed and asks for an echo; a second
        // flow opens it.
        Task<AmqpDeliveryState?> second = sender.SendAsync(new byte[] { 2 }, cancellationToken: timeout.Token).AsTask();
        await Task.Delay(100, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpFlowPerformative { NextIncomingId = seen, IncomingWindow = 0, NextOutgoingId = 1, OutgoingWindow = 100, Echo = true }, timeout.Token);
        AmqpTestFrame whileClosed = await peer.ReadNonEmptyFrameAsync(timeout.Token);
        bool sentWhileClosed = second.IsCompleted;
        await peer.WriteFrameAsync(0, new AmqpFlowPerformative { NextIncomingId = seen, IncomingWindow = 1, NextOutgoingId = 1, OutgoingWindow = 100 }, timeout.Token);
        AmqpTransferPerformative afterFlow = await ReadUntilAsync<AmqpTransferPerformative>(peer, timeout.Token);
        await second;

        // Assert
        whileClosed.Performative.ShouldBeOfType<AmqpFlowPerformative>().Handle.ShouldBeNull();
        sentWhileClosed.ShouldBeFalse();
        afterFlow.DeliveryId.ShouldBe(first.DeliveryId + 1);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReceiveAsync: Should restore the session's incoming window with a flow once the application took half of it")]
    public async Task ReceiveAsync_OnHalfIncomingWindowTaken_ShouldRestoreItWithFlow()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new() { SessionOptions = new AmqpSessionOptions { IncomingWindow = 4 } };
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(options, null, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);

        // Act: the peer's begin counts transfers from 1, and two deliveries the application has not taken hold half
        // the window.
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, settled: true), timeout.Token, payload: _payload);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 1, settled: true), timeout.Token, payload: _payload);
        AmqpFlowPerformative beforeTaking = await EchoSessionFlowAsync(peer, timeout.Token);
        await receiver.ReceiveAsync(timeout.Token);
        await receiver.ReceiveAsync(timeout.Token);
        AmqpFlowPerformative restored = await ReadSessionFlowAsync(peer, timeout.Token);

        // Assert: the window stayed half used until the application took both deliveries.
        engineBegin.IncomingWindow.ShouldBe(4u);
        beforeTaking.NextIncomingId.ShouldBe(3u);
        beforeTaking.IncomingWindow.ShouldBe(2u);
        restored.NextIncomingId.ShouldBe(3u);
        restored.IncomingWindow.ShouldBe(4u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - ReceiveAsync: Should keep the session's incoming window closed until the application takes a delivery")]
    public async Task ReceiveAsync_OnSlowConsumer_ShouldKeepIncomingWindowClosedUntilItTakesDeliveries()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new() { SessionOptions = new AmqpSessionOptions { IncomingWindow = 2 } };
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(options, null, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);

        // Act: two deliveries fill the window, and the application is slow to take them.
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, settled: true), timeout.Token, payload: _payload);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 1, settled: true), timeout.Token, payload: _payload);
        await Task.Delay(100, timeout.Token);
        AmqpFlowPerformative whileUntaken = await EchoSessionFlowAsync(peer, timeout.Token);
        IAmqpDelivery first = await receiver.ReceiveAsync(timeout.Token);
        AmqpFlowPerformative afterTaking = await ReadSessionFlowAsync(peer, timeout.Token);

        // Assert: taking one delivery frees half the window.
        whileUntaken.NextIncomingId.ShouldBe(3u);
        whileUntaken.IncomingWindow.ShouldBe(0u);
        first.Payload.ToArray().ShouldBe(_payload);
        afterTaking.NextIncomingId.ShouldBe(3u);
        afterTaking.IncomingWindow.ShouldBe(1u);
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should complete a delivery of more frames than the session's incoming window")]
    public async Task OnTransfer_OnDeliveryLargerThanIncomingWindow_ShouldCompleteIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new() { SessionOptions = new AmqpSessionOptions { IncomingWindow = 2 } };
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(options, null, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);
        List<uint> windows = [];

        // Act: the peer keeps to the window, sending each frame of five only once a flow lets it.
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0, settled: true)), timeout.Token, payload: new byte[] { 0 });

        for (byte index = 1; index < 5; index++)
        {
            windows.Add((await ReadSessionFlowAsync(peer, timeout.Token)).IncomingWindow);
            await peer.WriteFrameAsync(0, Continuation(more: index < 4), timeout.Token, payload: new byte[] { index });
        }

        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert: the frames of the delivery held no place until it was complete.
        windows.ShouldBe([2u, 2u, 2u, 2u]);
        delivery.Payload.ToArray().ShouldBe(new byte[] { 0, 1, 2, 3, 4 });
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DetachAsync: Should give back the places in the session's incoming window that a detached receiver's deliveries held")]
    public async Task DetachAsync_OnReceiverHoldingIncomingWindow_ShouldGiveItsPlacesBack()
    {
        // Arrange: two deliveries the application has not taken close the window.
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new() { SessionOptions = new AmqpSessionOptions { IncomingWindow = 2 } };
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(options, null, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, settled: true), timeout.Token, payload: _payload);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 1, settled: true), timeout.Token, payload: _payload);
        await SyncAsync(peer, timeout.Token);

        // Act
        Task detach = receiver.DetachAsync(cancellationToken: timeout.Token).AsTask();
        AmqpDetachPerformative engineDetach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);
        AmqpFlowPerformative restored = await ReadSessionFlowAsync(peer, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0 }, timeout.Token);
        await detach;

        // Assert: what the detached link still buffers can be taken, but holds the window no longer.
        engineDetach.Closed.ShouldBeFalse();
        restored.IncomingWindow.ShouldBe(2u);
        receiver.TryReceive(out IAmqpDelivery? buffered).ShouldBeTrue();
        buffered.ShouldNotBeNull().Payload.ToArray().ShouldBe(_payload);
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should end the session with amqp:session:window-violation when the peer sends past the incoming window")]
    public async Task OnTransfer_PastIncomingWindow_ShouldEndSessionWithWindowViolation()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new() { SessionOptions = new AmqpSessionOptions { IncomingWindow = 2 } };
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(options, null, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);

        // Act: the peer ignores the window and sends a third delivery while the first two are untaken.
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, settled: true), timeout.Token, payload: _payload);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 1, settled: true), timeout.Token, payload: _payload);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 2, settled: true), timeout.Token, payload: _payload);
        AmqpEndPerformative end = await ReadUntilAsync<AmqpEndPerformative>(peer, timeout.Token);

        // Assert
        end.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.WindowViolation);
        end.Error.Description.ShouldBe(ErrorMessages.WindowViolation);
        session.SessionEnded.IsCancellationRequested.ShouldBeTrue();
        host.Connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - EndAsync: Should take an end the peer sent at the same time as the answer to its own")]
    public async Task EndAsync_OnPeerEndCrossingIt_ShouldTakeItAsTheAnswer()
    {
        // Arrange: a close timeout longer than the test's, so only taking the peer's end as the answer ends the session in time.
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new() { HandshakeTimeout = TimeSpan.FromSeconds(30) };
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(options, null, timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);

        // Act: the peer ends the session for its own reason before it reads the engine's end, then begins a new
        // session on the channel.
        Task end = session.EndAsync(new AmqpError(AmqpErrorCondition.ResourceDeleted, "gone")).AsTask();
        await peer.WriteFrameAsync(0, new AmqpEndPerformative { Error = new AmqpError(AmqpErrorCondition.ResourceLimitExceeded) }, timeout.Token);
        await end.WaitAsync(timeout.Token);
        await peer.WriteFrameAsync(0, AmqpTestPeer.CreateBegin(), timeout.Token);
        AmqpTestFrame engineEnd = await peer.ReadNonEmptyFrameAsync(timeout.Token);
        AmqpTestFrame answer = await peer.ReadNonEmptyFrameAsync(timeout.Token);

        // Assert
        engineEnd.Performative.ShouldBeOfType<AmqpEndPerformative>().Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.ResourceDeleted);
        answer.Performative.ShouldBeOfType<AmqpBeginPerformative>().RemoteChannel.ShouldBe((ushort)0);
        session.SessionEnded.IsCancellationRequested.ShouldBeTrue();
        host.Connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnEnd: Should discard the peer's frames after ending the session with an error, until the peer ends it")]
    public async Task OnEnd_AfterSessionError_ShouldDiscardFramesUntilThePeerEnds()
    {
        // Arrange: a disposition whose last precedes its first ends the session with an error.
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative { Role = AmqpRole.Receiver, First = 5, Last = 2, Settled = true }, timeout.Token);
        AmqpEndPerformative end = await ReadUntilAsync<AmqpEndPerformative>(peer, timeout.Token);

        // Act: frames that would end a mapped session, the peer's end, and a new session on the channel.
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0), timeout.Token, payload: _payload);
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 6, Closed = true }, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpEndPerformative(), timeout.Token);
        await peer.WriteFrameAsync(0, AmqpTestPeer.CreateBegin(), timeout.Token);
        AmqpTestFrame answer = await peer.ReadNonEmptyFrameAsync(timeout.Token);

        // Assert: the engine wrote nothing between its end and the new session's begin.
        end.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        answer.Performative.ShouldBeOfType<AmqpBeginPerformative>().RemoteChannel.ShouldBe((ushort)0);
        answer.Header.Channel.ShouldBe((ushort)0);
        session.SessionEnded.IsCancellationRequested.ShouldBeTrue();
        host.Connection.State.ShouldBe(ConnectionState.Open);
    }

    /// <summary>
    /// Writes a session <c>flow</c> that asks for an echo and returns the first session <c>flow</c> the engine writes
    /// from then on, skipping the links': the echo, which the engine writes once it applied every frame before it,
    /// unless it restored its incoming window first.
    /// </summary>
    private static async Task<AmqpFlowPerformative> EchoSessionFlowAsync(AmqpTestPeer peer, CancellationToken cancellationToken)
    {
        await peer.WriteFrameAsync(0, new AmqpFlowPerformative { IncomingWindow = 100, NextOutgoingId = 1, OutgoingWindow = 100, Echo = true }, cancellationToken);

        return await ReadSessionFlowAsync(peer, cancellationToken);
    }

    /// <summary>
    /// Reads frames until the engine sends a session's <c>flow</c>, skipping the links'.
    /// </summary>
    private static async Task<AmqpFlowPerformative> ReadSessionFlowAsync(AmqpTestPeer peer, CancellationToken cancellationToken)
    {
        while (true)
        {
            AmqpFlowPerformative flow = await ReadUntilAsync<AmqpFlowPerformative>(peer, cancellationToken);

            if (flow.Handle is null)
            {
                return flow;
            }
        }
    }
}
