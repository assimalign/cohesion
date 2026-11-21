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
/// Drives the engine's links with a scripted peer through the rules of AMQP 1.0 Part 2 for a link: attaching it
/// under a name and a handle, refusing it, detaching or closing it, a link error, who may transfer on it, and its
/// <c>flow</c> state. Each rule in <see cref="Rules"/> names the tests that pin it.
/// </summary>
public class AmqpLinkComplianceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    private static readonly byte[] _payload = [0x00, 0x53, 0x77, 0x40];

    /// <summary>
    /// Gets the Part 2 link rules the engine holds, each with the tests that pin it.
    /// </summary>
    internal static IReadOnlyList<AmqpComplianceRule> Rules { get; } =
    [
        new("L1", "A link attaches with an attach from each end, each naming the link and its own role and choosing its own handle: the engine answers a peer's attach with the opposite role on the lowest handle it has free, and reads the peer's frames by the peer's handle.", "Part 2, sections 2.6.2, 2.6.3 and 2.7.3",
            Tests: [nameof(OnAttach_FromPeer_ShouldAnswerOnItsOwnHandle)],
            Refusals: []),
        new("L2", "A link's name identifies it among the links of its direction: the engine attaches a sender and a receiver that share a name, refuses a second attach of a name and direction it has attached on the session with amqp:not-allowed rather than stealing the link, and refuses one of its own before sending it.", "Part 2, section 2.6.1",
            Tests: [nameof(AttachAsync_OnSameNameInBothDirections_ShouldAttachBoth)],
            Refusals:
            [
                nameof(OnAttach_OnNameInUse_ShouldRefuseWithNotAllowed),
                nameof(AttachSenderAsync_OnNameInUse_ShouldThrowBeforeSending)
            ]),
        new("L3", "The engine answers a peer's attach on a handle no higher than the peer's handle-max: an attach it has no handle left to answer ends the session with amqp:resource-limit-exceeded.", "Part 2, sections 2.6.2 and 2.7.2",
            Tests: [],
            Refusals: [nameof(OnAttach_BeyondPeerHandleMax_ShouldEndSessionWithResourceLimitExceeded)]),
        new("L4", "An attach whose fields contradict each other, such as a sender's without an initial-delivery-count, refuses only its link with amqp:invalid-field, and the engine refuses one of its own before sending it.", "Part 2, sections 2.7.3 and 2.8.15",
            Tests: [nameof(AmqpAttachRulesTests.OnAttach_WithSenderNamingItsDynamicSource_ShouldAttachEchoingTheSource)],
            Refusals:
            [
                nameof(AmqpAttachRulesTests.OnAttach_WithContradictoryFields_ShouldRefuseLinkWithInvalidField),
                nameof(AmqpAttachRulesTests.AttachAsync_WithDynamicRequestAndAddress_ShouldThrowBeforeSending),
                nameof(AmqpAttachRulesTests.AcceptReceiverAsync_WithContradictoryTarget_ShouldThrowAndKeepTheRequestOpen)
            ]),
        new("L5", "A link is refused by answering its attach without the terminus the refusing end owns, then detaching it with the error.", "Part 2, section 2.6.3",
            Tests:
            [
                nameof(AmqpConnectionTests.RejectAsync_OnLinkRequest_ShouldFailAttachWithError),
                nameof(OnAttach_OnNameInUse_ShouldRefuseWithNotAllowed)
            ],
            Refusals: []),
        new("L6", "A detach is answered with a detach that carries the same closed flag: a closing detach ends the link, and one that does not close it suspends the link, whose name the peer may attach again.", "Part 2, sections 2.6.4, 2.6.6 and 2.7.7",
            Tests:
            [
                nameof(AmqpConnectionTests.CloseAsync_OnReceiver_ShouldDetachSenderAndFailUnsettledDelivery),
                nameof(DetachAsync_OnAttachedLink_ShouldSuspendItWithoutClosing),
                nameof(OnDetach_OnPeerSuspendingLink_ShouldAnswerWithoutClosing)
            ],
            Refusals: []),
        new("L7", "An error on a link detaches that link with the error and leaves its session and connection open.", "Part 2, section 2.6.5",
            Tests:
            [
                nameof(AmqpAttachRulesTests.OnAttach_WithContradictoryFields_ShouldRefuseLinkWithInvalidField),
                nameof(OnTransfer_AfterErrorDetach_ShouldDiscardItUntilThePeerDetaches)
            ],
            Refusals: []),
        new("L8", "Input the peer sent for a link before it read the engine's error detach is discarded until the peer's detach, not answered by ending the session with amqp:session:errant-link: frames in flight cannot be told from errant ones, and ending the session would end every other link on it.", "Part 2, sections 2.6.5 and 2.8.17",
            Tests: [nameof(OnTransfer_AfterErrorDetach_ShouldDiscardItUntilThePeerDetaches)],
            Refusals: [],
            Leniency: true),
        new("L9", "Only a link's sender transfers on it, and only within the credit its receiver granted: a transfer on a link the engine sends on detaches the link with amqp:illegal-state.", "Part 2, sections 2.6.7 and 2.7.5",
            Tests: [nameof(AmqpConnectionTests.ReceiveAsync_WithoutPrefetch_ShouldDeliverOnlyGrantedCredit)],
            Refusals: [nameof(OnTransfer_OnLinkThePeerReceivesOn_ShouldDetachWithIllegalState)]),
        new("L10", "A flow that asks for an echo is answered with the engine's flow state, the session's and for a link flow the link's as well, and the answer does not ask for an echo itself.", "Part 2, section 2.7.4",
            Tests:
            [
                nameof(OnFlow_WithEcho_ShouldAnswerWithTheLinkState),
                nameof(OnFlow_OnSessionFlowWithEcho_ShouldAnswerWithTheSessionState)
            ],
            Refusals: []),
        new("L11", "A session flow that carries link fields, which only a link flow may, is read as the session's: no handle says which link its link fields would belong to, so they are ignored.", "Part 2, section 2.7.4",
            Tests: [nameof(OnFlow_OnSessionFlowWithLinkFields_ShouldReadOnlyTheSessionState)],
            Refusals: [],
            Leniency: true)
    ];

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should answer a peer's attach in the opposite role on a handle of its own")]
    public async Task OnAttach_FromPeer_ShouldAnswerOnItsOwnHandle()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        AmqpAttachPerformative attach = CreateSenderAttach(new AmqpTarget { Address = "queue" });
        AmqpTransferPerformative transfer = CreateTransfer(deliveryId: 0, settled: true);
        attach.Name = "orders";
        attach.Handle = 5;
        transfer.Handle = 5;

        // Act
        await peer.WriteFrameAsync(0, attach, timeout.Token);
        IAmqpLinkRequest request = await session.AcceptLinkAsync(timeout.Token);
        IAmqpReceiver receiver = await request.AcceptReceiverAsync(cancellationToken: timeout.Token);
        AmqpAttachPerformative answer = await ReadUntilAsync<AmqpAttachPerformative>(peer, timeout.Token);
        await peer.WriteFrameAsync(0, transfer, timeout.Token, payload: _payload);
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        request.Name.ShouldBe("orders");
        request.Role.ShouldBe(AmqpRole.Receiver);
        answer.Name.ShouldBe("orders");
        answer.Role.ShouldBe(AmqpRole.Receiver);
        answer.Handle.ShouldBe(0u);
        receiver.Handle.ShouldBe(0u);
        delivery.Payload.ToArray().ShouldBe(_payload);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AttachAsync: Should attach a sender and a receiver that share a name")]
    public async Task AttachAsync_OnSameNameInBothDirections_ShouldAttachBoth()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        AmqpSenderOptions senderOptions = new() { Name = "pair", Target = new AmqpTarget { Address = "queue" } };
        (IAmqpSender sender, IAmqpReceiver serverReceiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, senderOptions, cancellationToken: timeout.Token);

        // Act
        Task<IAmqpLinkRequest> request = serverSession.AcceptLinkAsync(timeout.Token).AsTask();
        Task<IAmqpReceiver> attach = clientSession.AttachReceiverAsync(
            new AmqpReceiverOptions { Name = "pair", Source = new AmqpSource { Address = "queue" } },
            timeout.Token).AsTask();
        IAmqpSender serverSender = await (await request).AcceptSenderAsync(cancellationToken: timeout.Token);
        IAmqpReceiver receiver = await attach;

        // Assert
        sender.Name.ShouldBe("pair");
        receiver.Name.ShouldBe("pair");
        serverReceiver.Name.ShouldBe("pair");
        serverSender.Name.ShouldBe("pair");
        sender.LinkDetached.IsCancellationRequested.ShouldBeFalse();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should refuse a second link of a name and direction it has attached with not-allowed")]
    public async Task OnAttach_OnNameInUse_ShouldRefuseWithNotAllowed()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver first = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);
        AmqpAttachPerformative duplicate = CreateSenderAttach(new AmqpTarget { Address = "queue" });
        duplicate.Handle = 1;

        // Act
        await peer.WriteFrameAsync(0, duplicate, timeout.Token);
        AmqpAttachPerformative refusal = await ReadUntilAsync<AmqpAttachPerformative>(peer, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 1, Closed = true }, timeout.Token);
        AmqpPerformative next = await ReadNextAfterEchoAsync(peer, timeout.Token);

        // Assert
        refusal.Name.ShouldBe(duplicate.Name);
        refusal.Role.ShouldBe(AmqpRole.Receiver);
        refusal.Target.ShouldBeNull();
        detach.Handle.ShouldBe(refusal.Handle);
        detach.Closed.ShouldBeTrue();
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotAllowed);
        next.ShouldBeOfType<AmqpFlowPerformative>().Handle.ShouldBeNull();
        first.LinkDetached.IsCancellationRequested.ShouldBeFalse();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AttachSenderAsync: Should refuse a name it has attached in the same role before sending anything")]
    public async Task AttachSenderAsync_OnNameInUse_ShouldThrowBeforeSending()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        AmqpSenderOptions options = new() { Name = "orders", Target = new AmqpTarget { Address = "queue" } };
        Task<IAmqpSender> attach = session.AttachSenderAsync(options, timeout.Token).AsTask();
        AmqpAttachPerformative request = await ReadUntilAsync<AmqpAttachPerformative>(peer, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpAttachPerformative
        {
            Name = request.Name,
            Handle = 0,
            Role = AmqpRole.Receiver,
            Source = request.Source,
            Target = request.Target
        }, timeout.Token);
        IAmqpSender sender = await attach;

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(async () => await session.AttachSenderAsync(options, timeout.Token));
        AmqpPerformative next = await ReadNextAfterEchoAsync(peer, timeout.Token);

        // Assert
        exception.Message.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.LinkNameInUse, "orders"));
        next.ShouldBeOfType<AmqpFlowPerformative>().Handle.ShouldBeNull();
        sender.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should end the session when no handle within the peer's handle-max is left to answer an attach")]
    public async Task OnAttach_BeyondPeerHandleMax_ShouldEndSessionWithResourceLimitExceeded()
    {
        // Arrange: the peer accepts handle 0 alone, and the first link takes it.
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        await peer.BeginAsync(AmqpTestPeer.CreateBegin(handleMax: 0), timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);
        AmqpAttachPerformative second = CreateSenderAttach(new AmqpTarget { Address = "queue" });
        second.Name = "second";
        second.Handle = 1;

        // Act: a handle the engine accepts, which leaves it no handle of its own to answer with.
        await peer.WriteFrameAsync(0, second, timeout.Token);
        AmqpEndPerformative end = await ReadUntilAsync<AmqpEndPerformative>(peer, timeout.Token);

        // Assert
        AmqpError error = end.Error.ShouldNotBeNull();
        error.Condition.ShouldBe(AmqpErrorCondition.ResourceLimitExceeded);
        error.Description.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.HandlesExhausted, 0));
        session.SessionEnded.IsCancellationRequested.ShouldBeTrue();
        host.Connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DetachAsync: Should suspend the link with a detach that does not close it")]
    public async Task DetachAsync_OnAttachedLink_ShouldSuspendItWithoutClosing()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);

        // Act: the peer answers, then attaches the link again under its name and on its handle.
        Task detach = sender.DetachAsync(cancellationToken: timeout.Token).AsTask();
        AmqpDetachPerformative engineDetach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = false }, timeout.Token);
        await detach;
        await peer.WriteFrameAsync(0, CreateReceiverAttach(new AmqpTarget { Address = "peer" }), timeout.Token);
        IAmqpLinkRequest again = await session.AcceptLinkAsync(timeout.Token);

        // Assert
        engineDetach.Closed.ShouldBeFalse();
        engineDetach.Error.ShouldBeNull();
        sender.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        again.Name.ShouldBe("link");
        again.Role.ShouldBe(AmqpRole.Sender);
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDetach: Should answer a detach that does not close the link with one that does not either")]
    public async Task OnDetach_OnPeerSuspendingLink_ShouldAnswerWithoutClosing()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = false }, timeout.Token);
        AmqpDetachPerformative answer = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(receiver.ReceiveAsync(timeout.Token).AsTask());

        // Assert
        answer.Handle.ShouldBe(receiver.Handle);
        answer.Closed.ShouldBeFalse();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should discard a transfer that follows its error detach until the peer detaches, and keep the session")]
    public async Task OnTransfer_AfterErrorDetach_ShouldDiscardItUntilThePeerDetaches()
    {
        // Arrange: a transfer without credit detaches the link with an error.
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, new AmqpReceiverOptions { Prefetch = 0 }, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0), timeout.Token, payload: _payload);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Act: a transfer the peer sent before it read the detach, then the peer's detach.
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 1), timeout.Token, payload: _payload);
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = true }, timeout.Token);
        AmqpPerformative next = await ReadNextAfterEchoAsync(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.TransferLimitExceeded);
        next.ShouldBeOfType<AmqpFlowPerformative>().Handle.ShouldBeNull();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
        host.Connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link with illegal-state when the peer, its receiver, sends a transfer on it")]
    public async Task OnTransfer_OnLinkThePeerReceivesOn_ShouldDetachWithIllegalState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0), timeout.Token, payload: _payload);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Closed.ShouldBeTrue();
        AmqpError error = detach.Error.ShouldNotBeNull();
        error.Condition.ShouldBe(AmqpErrorCondition.IllegalState);
        error.Description.ShouldBe(ErrorMessages.LinkRoleUnexpected);
        sender.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnFlow: Should answer a link flow that asks for an echo with the link's flow state")]
    public async Task OnFlow_WithEcho_ShouldAnswerWithTheLinkState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);
        AmqpFlowPerformative flow = CreateLinkFlow(engineBegin, credit: 5);
        flow.Echo = true;

        // Act
        await peer.WriteFrameAsync(0, flow, timeout.Token);
        AmqpTestFrame answer = await peer.ReadNonEmptyFrameAsync(timeout.Token);

        // Assert: the peer's begin counts its transfers from 1.
        AmqpFlowPerformative state = answer.Performative.ShouldBeOfType<AmqpFlowPerformative>();
        state.Handle.ShouldBe(sender.Handle);
        state.DeliveryCount.ShouldBe(0u);
        state.LinkCredit.ShouldBe(5u);
        state.Available.ShouldBe(0u);
        state.NextIncomingId.ShouldBe(1u);
        state.Echo.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnFlow: Should answer a session flow that asks for an echo with the session's flow state")]
    public async Task OnFlow_OnSessionFlowWithEcho_ShouldAnswerWithTheSessionState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        await host.Connection.AcceptSessionAsync(timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpFlowPerformative
        {
            NextIncomingId = engineBegin.NextOutgoingId,
            IncomingWindow = 100,
            NextOutgoingId = 1,
            OutgoingWindow = 100,
            Echo = true
        }, timeout.Token);
        AmqpTestFrame answer = await peer.ReadNonEmptyFrameAsync(timeout.Token);

        // Assert
        AmqpFlowPerformative state = answer.Performative.ShouldBeOfType<AmqpFlowPerformative>();
        state.Handle.ShouldBeNull();
        state.NextIncomingId.ShouldBe(1u);
        state.NextOutgoingId.ShouldBe(engineBegin.NextOutgoingId);
        state.IncomingWindow.ShouldBe(engineBegin.IncomingWindow);
        state.Echo.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnFlow: Should read a session flow that carries link fields as the session's alone")]
    public async Task OnFlow_OnSessionFlowWithLinkFields_ShouldReadOnlyTheSessionState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);
        AmqpFlowPerformative flow = CreateLinkFlow(engineBegin, credit: 10);
        flow.Handle = null;
        flow.Echo = true;

        // Act: the flow grants credit but names no link.
        await peer.WriteFrameAsync(0, flow, timeout.Token);
        AmqpTestFrame answer = await peer.ReadNonEmptyFrameAsync(timeout.Token);

        // Assert
        answer.Performative.ShouldBeOfType<AmqpFlowPerformative>().Handle.ShouldBeNull();
        sender.Credit.ShouldBe(0u);
        sender.LinkDetached.IsCancellationRequested.ShouldBeFalse();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }
}
