using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        new("L2", "A link's name identifies it among the links of its direction: the engine attaches a sender and a receiver that share a name; unless its listener allows link stealing (L15), it keeps names per session, refuses a second attach of a name and direction it has attached on the session with amqp:not-allowed rather than stealing the link, leaves a link of the same name on another connection alone, and refuses one of its own before sending it.", "Part 2, section 2.6.1",
            Tests:
            [
                nameof(AttachAsync_OnSameNameInBothDirections_ShouldAttachBoth),
                nameof(OnAttach_OnLinkAttachedOnAnotherConnectionWithoutStealing_ShouldLeaveItAttached)
            ],
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
            Leniency: true),
        new("L12", "When the peer detaches without closing a link the engine closed, the peer's detach crossed the engine's, and the end that did not close attaches the link again and closes it: the engine answers that attach and closing detach as the end of its close, not as a new link, and waits for them for the close timeout, after which an attach of the name is a new link.", "Part 2, sections 2.6.6 and 2.7.7",
            Tests:
            [
                nameof(CloseAsync_OnLinkThePeerSuspendsMeanwhile_ShouldEndOnThePeersAttachAndClosingDetach),
                nameof(CloseAsync_OnLinkThePeerSuspendsAndNeverAttachesAgain_ShouldEndAtTheCloseTimeout)
            ],
            Refusals: []),
        new("L13", "When the peer closes a link the engine attached while the engine detaches it without closing, the engine attaches the link again, on its handle and with its termini but no unsettled map, and closes it once the peer answers; the application sees the link end.", "Part 2, sections 2.6.6 and 2.7.7",
            Tests:
            [
                nameof(DetachAsync_OnLinkItAttachedThatThePeerClosesMeanwhile_ShouldAttachItAgainAndCloseIt),
                nameof(AmqpNetLiteHostInteropTests.DetachAsync_OnLinkTheNetLiteHostCloses_ShouldAttachItAgainAndCloseIt)
            ],
            Refusals: []),
        new("L14", "A closing detach that answers the engine's non-closing detach of a link the peer attached ends the link, and the engine does not attach it again to close it: such an answer cannot be told from a crossing, AMQPNetLite answers every non-closing detach that way, and a peer that only initiates links can refuse an attach it did not ask for, as an AMQPNetLite client does by closing the connection with amqp:not-found.", "Part 2, sections 2.6.6 and 2.7.7",
            Tests:
            [
                nameof(DetachAsync_OnLinkThePeerAttachedThatThePeerCloses_ShouldEndItWithoutAttachingAgain),
                nameof(AmqpNetLiteClientInteropTests.DetachAsync_OnNetLiteClientLink_ShouldEndItAndKeepTheConnection)
            ],
            Refusals: [],
            Leniency: true),
        new("L15", "With link stealing on, a listener holds one attachment of each link across its connections, identified by both container identifiers, the link's name and its direction: a peer's attach of a link attached on another connection or session takes the link over, and the engine closes the earlier attachment with amqp:link:stolen and fails what waits on it, closing one the application is still attaching once the peer answers its attach; of two attaches at once, the one the listener handles second keeps the link; a peer whose suspending detach crossed the closing one attaches the link again on its connection to close it (L12), which ends the close without stealing the link back; a suspended link is no longer attached, so attaching it again resumes it rather than stealing it; and the application's own attach of a link attached elsewhere is refused before it is sent.", "Part 2, sections 2.6.1, 2.6.6 and 2.8.18",
            Tests:
            [
                nameof(OnAttach_OnLinkAttachedOnAnotherConnection_ShouldStealItWithLinkStolen),
                nameof(OnAttach_OnNameInUseWithLinkStealing_ShouldStealTheLinkOnItsSession),
                nameof(OnAttach_OnConcurrentAttachesWithLinkStealing_ShouldKeepOneAttachment),
                nameof(OnAttach_OnStolenLinkThePeerSuspendsMeanwhile_ShouldEndTheCloseWithoutStealingItBack),
                nameof(OnAttach_OnSuspendedLinkWithLinkStealing_ShouldResumeItWithoutStealing),
                nameof(OnAttach_OnLinkTheApplicationIsAttachingElsewhere_ShouldFailItsAttachAndCloseItOnceAnswered)
            ],
            Refusals: [nameof(AttachSenderAsync_OnLinkAttachedOnAnotherConnection_ShouldThrowBeforeSending)])
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

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - CloseAsync: Should end its close on the attach and closing detach of a peer whose suspending detach crossed it")]
    public async Task CloseAsync_OnLinkThePeerSuspendsMeanwhile_ShouldEndOnThePeersAttachAndClosingDetach()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        AmqpAttachPerformative peerAttach = CreateReceiverAttach(new AmqpTarget { Address = "peer" });
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, peerAttach, null, timeout.Token);

        // Act: the peer suspends the link as the engine's closing detach reaches it, then attaches the link again
        // and closes it.
        Task close = sender.CloseAsync(cancellationToken: timeout.Token).AsTask();
        AmqpDetachPerformative engineClose = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = false }, timeout.Token);
        await SyncAsync(peer, timeout.Token);
        bool endedBeforeTheAttach = close.IsCompleted;
        await peer.WriteFrameAsync(0, peerAttach, timeout.Token);
        AmqpAttachPerformative answer = await ReadUntilAsync<AmqpAttachPerformative>(peer, timeout.Token);
        AmqpPerformative closing = (await peer.ReadNonEmptyFrameAsync(timeout.Token)).Performative!;
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = true }, timeout.Token);
        await close;
        IAmqpLinkRequest next = await AcceptProbeAsync(peer, session, timeout.Token);

        // Assert: the engine answers the attach and closes the link at once, and raises no link request for it.
        engineClose.Closed.ShouldBeTrue();
        endedBeforeTheAttach.ShouldBeFalse();
        answer.Name.ShouldBe("link");
        answer.Role.ShouldBe(AmqpRole.Sender);
        answer.Handle.ShouldBe(sender.Handle);
        answer.Source.ShouldNotBeNull().Address.ShouldBe("queue");
        AmqpDetachPerformative detach = closing.ShouldBeOfType<AmqpDetachPerformative>();
        detach.Handle.ShouldBe(sender.Handle);
        detach.Closed.ShouldBeTrue();
        detach.Error.ShouldBeNull();
        next.Name.ShouldBe("probe");
        sender.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - CloseAsync: Should end at the close timeout a close whose crossing peer never attaches the link again, and free the link's name")]
    public async Task CloseAsync_OnLinkThePeerSuspendsAndNeverAttachesAgain_ShouldEndAtTheCloseTimeout()
    {
        // Arrange: a close timeout of two seconds.
        using CancellationTokenSource timeout = new(_testTimeout);
        AmqpConnectionListenerOptions options = new() { HandshakeTimeout = TimeSpan.FromSeconds(2) };
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(options, cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        AmqpAttachPerformative peerAttach = CreateReceiverAttach(new AmqpTarget { Address = "peer" });
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, peerAttach, null, timeout.Token);

        // Act: the peer suspends the link as the engine's closing detach reaches it, and attaches the link again
        // only once the close ended.
        Task close = sender.CloseAsync(cancellationToken: timeout.Token).AsTask();
        await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = false }, timeout.Token);
        await close;
        await peer.WriteFrameAsync(0, peerAttach, timeout.Token);
        IAmqpLinkRequest request = await session.AcceptLinkAsync(timeout.Token);

        // Assert: an attach that comes after the close ended is a new link.
        request.Name.ShouldBe("link");
        request.Role.ShouldBe(AmqpRole.Sender);
        sender.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DetachAsync: Should attach a link it attached again and close it when the peer's closing detach crosses its suspending one")]
    public async Task DetachAsync_OnLinkItAttachedThatThePeerClosesMeanwhile_ShouldAttachItAgainAndCloseIt()
    {
        // Arrange: the engine attaches a receiver, which grants its prefetch credit once the peer answers.
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        Task<IAmqpReceiver> attach = session.AttachReceiverAsync(
            new AmqpReceiverOptions { Name = "orders", Source = new AmqpSource { Address = "queue" } },
            timeout.Token).AsTask();
        AmqpAttachPerformative engineAttach = await ReadUntilAsync<AmqpAttachPerformative>(peer, timeout.Token);
        AmqpAttachPerformative peerAttach = new()
        {
            Name = "orders",
            Handle = 0,
            Role = AmqpRole.Sender,
            Source = engineAttach.Source,
            Target = engineAttach.Target,
            InitialDeliveryCount = 0
        };
        await peer.WriteFrameAsync(0, peerAttach, timeout.Token);
        IAmqpReceiver receiver = await attach;
        await SyncAsync(peer, timeout.Token);

        // Act: the peer closes the link as the engine's suspending detach reaches it, answers the engine's attach,
        // then answers its closing detach.
        Task detach = receiver.DetachAsync(cancellationToken: timeout.Token).AsTask();
        AmqpDetachPerformative suspend = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = true }, timeout.Token);
        AmqpAttachPerformative reattach = await ReadUntilAsync<AmqpAttachPerformative>(peer, timeout.Token);
        await peer.WriteFrameAsync(0, peerAttach, timeout.Token);
        AmqpPerformative closing = (await peer.ReadNonEmptyFrameAsync(timeout.Token)).Performative!;
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = true }, timeout.Token);
        await detach;
        IAmqpLinkRequest next = await AcceptProbeAsync(peer, session, timeout.Token);

        // Assert: the attach names the link and its terminus on the same handle and resumes nothing, and the closing
        // detach follows the peer's answer at once, with no credit granted in between.
        suspend.Closed.ShouldBeFalse();
        reattach.Name.ShouldBe("orders");
        reattach.Role.ShouldBe(AmqpRole.Receiver);
        reattach.Handle.ShouldBe(engineAttach.Handle);
        reattach.Source.ShouldNotBeNull().Address.ShouldBe("queue");
        reattach.Unsettled.ShouldBeNull();
        AmqpDetachPerformative close = closing.ShouldBeOfType<AmqpDetachPerformative>();
        close.Handle.ShouldBe(engineAttach.Handle);
        close.Closed.ShouldBeTrue();
        close.Error.ShouldBeNull();
        next.Name.ShouldBe("probe");
        receiver.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DetachAsync: Should end a link the peer attached at a closing detach that answers its suspending one, without attaching it again")]
    public async Task DetachAsync_OnLinkThePeerAttachedThatThePeerCloses_ShouldEndItWithoutAttachingAgain()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);

        // Act: the peer answers the engine's suspending detach by closing the link, as AMQPNetLite always does.
        Task detach = sender.DetachAsync(cancellationToken: timeout.Token).AsTask();
        AmqpDetachPerformative suspend = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = true }, timeout.Token);
        await detach;
        AmqpPerformative next = await ReadNextAfterEchoAsync(peer, timeout.Token);

        // Assert: the next frame after the detaches is the echo, not an attach.
        suspend.Closed.ShouldBeFalse();
        next.ShouldBeOfType<AmqpFlowPerformative>().Handle.ShouldBeNull();
        sender.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should leave a link attached on another connection alone when the listener does not allow link stealing")]
    public async Task OnAttach_OnLinkAttachedOnAnotherConnectionWithoutStealing_ShouldLeaveItAttached()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpScriptedHost firstHost, AmqpScriptedHost secondHost) = await AmqpScriptedHost.AcceptPairAsync(null, timeout.Token);
        await using AmqpScriptedHost first = firstHost;
        await using AmqpScriptedHost second = secondHost;
        IAmqpSession firstSession = await BeginAsync(first, timeout.Token);
        IAmqpSession secondSession = await BeginAsync(second, timeout.Token);
        AmqpAttachPerformative attach = CreateSenderAttach(new AmqpTarget { Address = "queue" });
        attach.Name = "orders";
        IAmqpReceiver earlier = await AttachPeerSenderAsync(first.Peer, firstSession, attach, null, timeout.Token);

        // Act
        IAmqpReceiver later = await AttachPeerSenderAsync(second.Peer, secondSession, attach, null, timeout.Token);
        List<string> firstFrames = await ReadThroughEchoAsync(first.Peer, timeout.Token);

        // Assert
        ((AmqpConnectionEndpoint)first.Connection).LinkRegistry.ShouldBeNull();
        firstFrames.ShouldNotContain("detach");
        earlier.LinkDetached.IsCancellationRequested.ShouldBeFalse();
        later.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should steal a link attached on another of the listener's connections, closing it with link-stolen")]
    public async Task OnAttach_OnLinkAttachedOnAnotherConnection_ShouldStealItWithLinkStolen()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpScriptedHost firstHost, AmqpScriptedHost secondHost) = await AmqpScriptedHost.AcceptPairAsync(new AmqpConnectionListenerOptions { AllowLinkStealing = true }, timeout.Token);
        await using AmqpScriptedHost first = firstHost;
        await using AmqpScriptedHost second = secondHost;
        AmqpLinkRegistry registry = ((AmqpConnectionEndpoint)first.Connection).LinkRegistry.ShouldNotBeNull();
        IAmqpSession firstSession = await BeginAsync(first, timeout.Token);
        IAmqpSession secondSession = await BeginAsync(second, timeout.Token);
        AmqpAttachPerformative attach = CreateSenderAttach(new AmqpTarget { Address = "queue" });
        attach.Name = "orders";
        IAmqpReceiver stolen = await AttachPeerSenderAsync(first.Peer, firstSession, attach, null, timeout.Token);
        Task<IAmqpDelivery> pendingReceive = stolen.ReceiveAsync(timeout.Token).AsTask();

        // Act: the peer attaches the link again from its second connection, as a client that reconnected does.
        IAmqpReceiver taker = await AttachPeerSenderAsync(second.Peer, secondSession, attach, null, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(first.Peer, timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(pendingReceive);
        await first.Peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = true }, timeout.Token);
        AmqpPerformative next = await ReadNextAfterEchoAsync(first.Peer, timeout.Token);
        await second.Peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, settled: true), timeout.Token, payload: _payload);
        IAmqpDelivery delivery = await taker.ReceiveAsync(timeout.Token);

        // Assert: the old attachment closed with the condition and nothing followed it; the new one carries the link.
        detach.Handle.ShouldBe(stolen.Handle);
        detach.Closed.ShouldBeTrue();
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.Stolen);
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.Stolen);
        stolen.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        next.ShouldBeOfType<AmqpFlowPerformative>().Handle.ShouldBeNull();
        delivery.Payload.ToArray().ShouldBe(_payload);
        registry.Count.ShouldBe(1);
        firstSession.SessionEnded.IsCancellationRequested.ShouldBeFalse();
        first.Connection.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should steal a link of the same name and direction on its session when the listener allows link stealing")]
    public async Task OnAttach_OnNameInUseWithLinkStealing_ShouldStealTheLinkOnItsSession()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(new AmqpConnectionListenerOptions { AllowLinkStealing = true }, cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpSender stolen = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);
        Task<AmqpDeliveryState?> pendingSend = stolen.SendAsync(_payload, cancellationToken: timeout.Token).AsTask();
        AmqpAttachPerformative again = CreateReceiverAttach(new AmqpTarget { Address = "peer" });
        again.Handle = 1;

        // Act
        await peer.WriteFrameAsync(0, again, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(pendingSend);
        IAmqpLinkRequest request = await session.AcceptLinkAsync(timeout.Token);
        IAmqpSender taker = await request.AcceptSenderAsync(cancellationToken: timeout.Token);
        AmqpAttachPerformative answer = await ReadUntilAsync<AmqpAttachPerformative>(peer, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = true }, timeout.Token);
        AmqpPerformative next = await ReadNextAfterEchoAsync(peer, timeout.Token);

        // Assert
        detach.Handle.ShouldBe(stolen.Handle);
        detach.Closed.ShouldBeTrue();
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.Stolen);
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.Stolen);
        request.Name.ShouldBe("link");
        answer.Name.ShouldBe("link");
        answer.Handle.ShouldBe(taker.Handle);
        taker.Handle.ShouldNotBe(stolen.Handle);
        next.ShouldBeOfType<AmqpFlowPerformative>().Handle.ShouldBeNull();
        taker.LinkDetached.IsCancellationRequested.ShouldBeFalse();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should keep one attachment of a link two connections attach at once, and steal the other")]
    public async Task OnAttach_OnConcurrentAttachesWithLinkStealing_ShouldKeepOneAttachment()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpScriptedHost firstHost, AmqpScriptedHost secondHost) = await AmqpScriptedHost.AcceptPairAsync(new AmqpConnectionListenerOptions { AllowLinkStealing = true }, timeout.Token);
        await using AmqpScriptedHost first = firstHost;
        await using AmqpScriptedHost second = secondHost;
        AmqpLinkRegistry registry = ((AmqpConnectionEndpoint)first.Connection).LinkRegistry.ShouldNotBeNull();
        IAmqpSession firstSession = await BeginAsync(first, timeout.Token);
        IAmqpSession secondSession = await BeginAsync(second, timeout.Token);
        AmqpAttachPerformative attach = CreateSenderAttach(new AmqpTarget { Address = "queue" });
        attach.Name = "orders";

        // Act: both attaches race, and the application answers both requests.
        await Task.WhenAll(first.Peer.WriteFrameAsync(0, attach, timeout.Token), second.Peer.WriteFrameAsync(0, attach, timeout.Token));
        IAmqpLinkRequest firstRequest = await firstSession.AcceptLinkAsync(timeout.Token);
        IAmqpLinkRequest secondRequest = await secondSession.AcceptLinkAsync(timeout.Token);
        IAmqpReceiver?[] receivers =
        [
            await AcceptUnlessStolenAsync(firstRequest, timeout.Token),
            await AcceptUnlessStolenAsync(secondRequest, timeout.Token)
        ];
        int stolenIndex = await WaitForStolenAsync(receivers, timeout.Token);
        AmqpScriptedHost stolenHost = stolenIndex == 0 ? first : second;
        AmqpScriptedHost keptHost = stolenIndex == 0 ? second : first;
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(stolenHost.Peer, timeout.Token);
        List<string> keptFrames = await ReadThroughEchoAsync(keptHost.Peer, timeout.Token);

        // Assert: exactly one attachment ended, with the condition, and the other kept the link.
        detach.Closed.ShouldBeTrue();
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.Stolen);
        keptFrames.ShouldNotContain("detach");
        IAmqpReceiver kept = receivers[1 - stolenIndex].ShouldNotBeNull();
        kept.LinkDetached.IsCancellationRequested.ShouldBeFalse();
        registry.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should end a stolen link's close on the attach and closing detach of a peer whose suspending detach crossed it, without stealing the link back")]
    public async Task OnAttach_OnStolenLinkThePeerSuspendsMeanwhile_ShouldEndTheCloseWithoutStealingItBack()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpScriptedHost firstHost, AmqpScriptedHost secondHost) = await AmqpScriptedHost.AcceptPairAsync(new AmqpConnectionListenerOptions { AllowLinkStealing = true }, timeout.Token);
        await using AmqpScriptedHost first = firstHost;
        await using AmqpScriptedHost second = secondHost;
        IAmqpSession firstSession = await BeginAsync(first, timeout.Token);
        IAmqpSession secondSession = await BeginAsync(second, timeout.Token);
        AmqpAttachPerformative attach = CreateSenderAttach(new AmqpTarget { Address = "queue" });
        attach.Name = "orders";
        IAmqpReceiver stolen = await AttachPeerSenderAsync(first.Peer, firstSession, attach, null, timeout.Token);
        IAmqpReceiver taker = await AttachPeerSenderAsync(second.Peer, secondSession, attach, null, timeout.Token);
        AmqpDetachPerformative stolenClose = await ReadUntilAsync<AmqpDetachPerformative>(first.Peer, timeout.Token);

        // Act: the peer had suspended the old attachment meanwhile, so it attaches the link again there to close it.
        await first.Peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = false }, timeout.Token);
        await first.Peer.WriteFrameAsync(0, attach, timeout.Token);
        AmqpAttachPerformative answer = await ReadUntilAsync<AmqpAttachPerformative>(first.Peer, timeout.Token);
        AmqpDetachPerformative close = await ReadUntilAsync<AmqpDetachPerformative>(first.Peer, timeout.Token);
        await first.Peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = true }, timeout.Token);
        IAmqpLinkRequest probe = await AcceptProbeAsync(first.Peer, firstSession, timeout.Token);
        List<string> takerFrames = await ReadThroughEchoAsync(second.Peer, timeout.Token);

        // Assert: the attach ended the old close and raised no request, and the new attachment keeps the link.
        stolenClose.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.Stolen);
        answer.Name.ShouldBe("orders");
        answer.Handle.ShouldBe(stolen.Handle);
        close.Closed.ShouldBeTrue();
        probe.Name.ShouldBe("probe");
        takerFrames.ShouldNotContain("detach");
        taker.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should resume, not steal, a link the peer suspended before attaching it again on another connection")]
    public async Task OnAttach_OnSuspendedLinkWithLinkStealing_ShouldResumeItWithoutStealing()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpScriptedHost firstHost, AmqpScriptedHost secondHost) = await AmqpScriptedHost.AcceptPairAsync(new AmqpConnectionListenerOptions { AllowLinkStealing = true }, timeout.Token);
        await using AmqpScriptedHost first = firstHost;
        await using AmqpScriptedHost second = secondHost;
        AmqpLinkRegistry registry = ((AmqpConnectionEndpoint)first.Connection).LinkRegistry.ShouldNotBeNull();
        IAmqpSession firstSession = await BeginAsync(first, timeout.Token);
        IAmqpSession secondSession = await BeginAsync(second, timeout.Token);
        AmqpAttachPerformative attach = CreateSenderAttach(new AmqpTarget { Address = "queue" });
        attach.Name = "orders";
        IAmqpReceiver suspended = await AttachPeerSenderAsync(first.Peer, firstSession, attach, null, timeout.Token);
        await first.Peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = false }, timeout.Token);
        AmqpDetachPerformative answer = await ReadUntilAsync<AmqpDetachPerformative>(first.Peer, timeout.Token);
        int listedWhileSuspended = registry.Count;
        AmqpAttachPerformative resume = CreateSenderAttach(new AmqpTarget { Address = "queue" });
        resume.Name = "orders";
        resume.Unsettled = new Dictionary<AmqpDeliveryTag, AmqpDeliveryState?>();

        // Act
        await second.Peer.WriteFrameAsync(0, resume, timeout.Token);
        IAmqpLinkRequest request = await secondSession.AcceptLinkAsync(timeout.Token);
        IAmqpReceiver resumed = await request.AcceptReceiverAsync(cancellationToken: timeout.Token);
        AmqpPerformative next = await ReadNextAfterEchoAsync(first.Peer, timeout.Token);

        // Assert: nothing reached the first connection, and the suspension, not a steal, ended the first attachment.
        answer.Closed.ShouldBeFalse();
        listedWhileSuspended.ShouldBe(0);
        request.Unsettled.ShouldNotBeNull().ShouldBeEmpty();
        next.ShouldBeOfType<AmqpFlowPerformative>().Handle.ShouldBeNull();
        suspended.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        resumed.LinkDetached.IsCancellationRequested.ShouldBeFalse();
        AmqpException reason = await Should.ThrowAsync<AmqpException>(async () => await suspended.ReceiveAsync(timeout.Token));
        reason.Error.ShouldBeNull();
        registry.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should fail the application's unanswered attach of a link the peer attaches on another connection, and close that link with link-stolen once the peer answers")]
    public async Task OnAttach_OnLinkTheApplicationIsAttachingElsewhere_ShouldFailItsAttachAndCloseItOnceAnswered()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpScriptedHost firstHost, AmqpScriptedHost secondHost) = await AmqpScriptedHost.AcceptPairAsync(new AmqpConnectionListenerOptions { AllowLinkStealing = true }, timeout.Token);
        await using AmqpScriptedHost first = firstHost;
        await using AmqpScriptedHost second = secondHost;
        AmqpLinkRegistry registry = ((AmqpConnectionEndpoint)first.Connection).LinkRegistry.ShouldNotBeNull();
        IAmqpSession firstSession = await BeginAsync(first, timeout.Token);
        IAmqpSession secondSession = await BeginAsync(second, timeout.Token);
        AmqpSenderOptions options = new() { Name = "orders", Target = new AmqpTarget { Address = "peer" } };
        Task<IAmqpSender> pendingAttach = firstSession.AttachSenderAsync(options, timeout.Token).AsTask();
        AmqpAttachPerformative engineAttach = await ReadUntilAsync<AmqpAttachPerformative>(first.Peer, timeout.Token);
        AmqpAttachPerformative attach = CreateReceiverAttach(new AmqpTarget { Address = "peer" });
        attach.Name = "orders";

        // Act: the peer attaches the link from its second connection before its first answers the engine's attach.
        await second.Peer.WriteFrameAsync(0, attach, timeout.Token);
        IAmqpLinkRequest request = await secondSession.AcceptLinkAsync(timeout.Token);
        IAmqpSender taker = await request.AcceptSenderAsync(cancellationToken: timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(pendingAttach);
        await first.Peer.WriteFrameAsync(0, new AmqpAttachPerformative
        {
            Name = engineAttach.Name,
            Handle = 0,
            Role = AmqpRole.Receiver,
            Source = engineAttach.Source,
            Target = engineAttach.Target
        }, timeout.Token);
        List<AmqpPerformative> answered = await ReadPerformativesThroughEchoAsync(first.Peer, timeout.Token);
        await first.Peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = true }, timeout.Token);
        AmqpPerformative next = await ReadNextAfterEchoAsync(first.Peer, timeout.Token);

        // Assert: the application's attach failed with the condition, the engine closed the attachment with it once
        // the peer answered, and the new attachment keeps the link.
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.Stolen);
        AmqpDetachPerformative close = answered.OfType<AmqpDetachPerformative>().ShouldHaveSingleItem();
        close.Handle.ShouldBe(engineAttach.Handle);
        close.Closed.ShouldBeTrue();
        close.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.Stolen);
        next.ShouldBeOfType<AmqpFlowPerformative>().Handle.ShouldBeNull();
        request.Name.ShouldBe("orders");
        taker.LinkDetached.IsCancellationRequested.ShouldBeFalse();
        registry.Count.ShouldBe(1);
        firstSession.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AttachSenderAsync: Should refuse a link the peer attached on another of the listener's connections before sending anything")]
    public async Task AttachSenderAsync_OnLinkAttachedOnAnotherConnection_ShouldThrowBeforeSending()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        (AmqpScriptedHost firstHost, AmqpScriptedHost secondHost) = await AmqpScriptedHost.AcceptPairAsync(new AmqpConnectionListenerOptions { AllowLinkStealing = true }, timeout.Token);
        await using AmqpScriptedHost first = firstHost;
        await using AmqpScriptedHost second = secondHost;
        IAmqpSession firstSession = await BeginAsync(first, timeout.Token);
        IAmqpSession secondSession = await BeginAsync(second, timeout.Token);
        AmqpAttachPerformative attach = CreateReceiverAttach(new AmqpTarget { Address = "peer" });
        attach.Name = "orders";
        IAmqpSender attached = await AttachPeerReceiverAsync(first.Peer, firstSession, attach, null, timeout.Token);
        AmqpSenderOptions options = new() { Name = "orders", Target = new AmqpTarget { Address = "peer" } };

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(async () => await secondSession.AttachSenderAsync(options, timeout.Token));
        AmqpPerformative next = await ReadNextAfterEchoAsync(second.Peer, timeout.Token);

        // Assert: the second connection sent nothing, and the first keeps the link.
        exception.Message.ShouldBe(string.Format(CultureInfo.CurrentCulture, ErrorMessages.LinkNameInUse, "orders"));
        next.ShouldBeOfType<AmqpFlowPerformative>().Handle.ShouldBeNull();
        attached.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    /// <summary>
    /// Accepts a link request as a receiver, or returns <see langword="null"/> when an attach on another connection
    /// stole the link before the application answered.
    /// </summary>
    private static async Task<IAmqpReceiver?> AcceptUnlessStolenAsync(IAmqpLinkRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await request.AcceptReceiverAsync(cancellationToken: cancellationToken);
        }
        catch (AmqpException exception) when (exception.Error?.Condition == AmqpErrorCondition.Stolen)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the index of the receiver whose link was stolen: one whose request could not be accepted, or the first
    /// whose link detaches.
    /// </summary>
    private static async Task<int> WaitForStolenAsync(IAmqpReceiver?[] receivers, CancellationToken cancellationToken)
    {
        int unanswered = Array.FindIndex(receivers, receiver => receiver is null);

        if (unanswered >= 0)
        {
            return unanswered;
        }

        Task[] detached = Array.ConvertAll(receivers, receiver => Task.Delay(Timeout.Infinite, receiver!.LinkDetached));
        Task first = await Task.WhenAny(detached).WaitAsync(cancellationToken);

        return Array.IndexOf(detached, first);
    }

    /// <summary>
    /// Writes a session <c>flow</c> that asks for an echo, and returns the names of the performatives the engine
    /// wrote up to and including that echo.
    /// </summary>
    private static async Task<List<string>> ReadThroughEchoAsync(AmqpTestPeer peer, CancellationToken cancellationToken)
    {
        List<AmqpPerformative> performatives = await ReadPerformativesThroughEchoAsync(peer, cancellationToken);

        return performatives.ConvertAll(performative => performative.PerformativeName);
    }

    /// <summary>
    /// Writes a session <c>flow</c> that asks for an echo, and returns the performatives the engine wrote up to and
    /// including that echo.
    /// </summary>
    private static async Task<List<AmqpPerformative>> ReadPerformativesThroughEchoAsync(AmqpTestPeer peer, CancellationToken cancellationToken)
    {
        await peer.WriteFrameAsync(0, new AmqpFlowPerformative { IncomingWindow = 100, NextOutgoingId = 1, OutgoingWindow = 100, Echo = true }, cancellationToken);

        List<AmqpPerformative> performatives = [];

        while (true)
        {
            AmqpPerformative performative = (await peer.ReadNonEmptyFrameAsync(cancellationToken)).Performative!;

            performatives.Add(performative);

            if (performative is AmqpFlowPerformative { Handle: null })
            {
                return performatives;
            }
        }
    }

    /// <summary>
    /// The peer attaches a sending link named <c>probe</c>; returns the first link request the session raises, which
    /// is the probe's only when the session raised none before it.
    /// </summary>
    private static async Task<IAmqpLinkRequest> AcceptProbeAsync(AmqpTestPeer peer, IAmqpSession session, CancellationToken cancellationToken)
    {
        AmqpAttachPerformative probe = CreateSenderAttach(new AmqpTarget { Address = "queue" });

        probe.Name = "probe";
        probe.Handle = 7;
        await peer.WriteFrameAsync(0, probe, cancellationToken);
        return await session.AcceptLinkAsync(cancellationToken);
    }
}
