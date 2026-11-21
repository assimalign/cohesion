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

using static Assimalign.Cohesion.Amqp.Connections.Tests.AmqpPeerScript;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Drives deliveries to settlement through the rules of AMQP 1.0 Part 2 and Part 3: the outcome that ends a
/// delivery, the settlement modes a link negotiates, and the range a <c>disposition</c> names. Each rule in
/// <see cref="Rules"/> names the tests that pin it.
/// </summary>
public class AmqpSettlementComplianceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the settlement rules the engine holds, each with the tests that pin it.
    /// </summary>
    internal static IReadOnlyList<AmqpComplianceRule> Rules { get; } =
    [
        new("DS1", "A delivery sent unsettled ends with the receiver's terminal outcome: the send completes with accepted, rejected, released or modified, as the receiver stated it.", "Part 2, sections 2.6.12 and 2.7.6; Part 3, sections 3.4.2 to 3.4.5",
            Tests:
            [
                nameof(AmqpConnectionTests.SendAsync_OnAcceptedDelivery_ShouldCompleteWithAcceptedOutcome),
                nameof(AmqpConnectionTests.SendAsync_OnTerminalOutcome_ShouldCompleteWithThatOutcome)
            ],
            Refusals: []),
        new("DS2", "Under sender settle mode settled every delivery goes settled, under unsettled every delivery goes unsettled, and mixed lets each delivery choose: the engine refuses a delivery whose settled flag contradicts its link's mode before sending it.", "Part 2, section 2.8.2",
            Tests: [nameof(AmqpConnectionTests.SendAsync_OnSettledLink_ShouldCompleteWithoutOutcome)],
            Refusals: [nameof(SendAsync_WithSettledFlagAgainstSenderSettleMode_ShouldThrow)]),
        new("DS3", "Under receiver settle mode second the receiver states its outcome without settling, the sender settles, and the receiver settles once it sees that; a transfer may not ask for mode second on a link that settles first, which detaches the link with amqp:invalid-field.", "Part 2, sections 2.6.12, 2.7.5 and 2.8.3",
            Tests: [nameof(AmqpConnectionTests.AcceptAsync_OnReceiverSettleModeSecond_ShouldCompleteWhenSenderSettles)],
            Refusals: [nameof(OnTransfer_WithReceiverSettleModeSecondOnFirstLink_ShouldDetachWithInvalidField)]),
        new("DS4", "A disposition names its deliveries as the range first to last, where an absent last means first alone: the engine writes a single delivery without last, reads a range, and ends a session whose peer names a last before its first with amqp:invalid-field.", "Part 2, section 2.7.6",
            Tests:
            [
                nameof(AmqpPerformativeTests.Encode_WithSingleDeliveryDisposition_ShouldOmitLast),
                nameof(AmqpPerformativeTests.Decode_WithDispositionRange_ShouldRoundTrip)
            ],
            Refusals: [nameof(AmqpEngineViolationTests.OnDisposition_OnLastBeforeFirst_ShouldEndSessionWithInvalidField)]),
        new("DS5", "A disposition applies to the deliveries the engine holds unsettled within its range: an id it never sent or settled already is passed over, and a delivery sent after the disposition arrived is not covered by it.", "Part 2, section 2.7.6",
            Tests: [nameof(OnDisposition_OnRangeBeyondUnsettledDeliveries_ShouldSettleOnlyThoseItHolds)],
            Refusals: []),
        new("DS6", "A peer's transfer is taken as settled or unsettled by its own settled flag, even where that contradicts the sender settle mode the peer declared: the flag states what the sender did, and refusing it would strand the delivery.", "Part 2, sections 2.7.5 and 2.8.2",
            Tests: [nameof(OnTransfer_OnSettledFlagAgainstSenderSettleMode_ShouldTakeTheFlag)],
            Refusals: [],
            Leniency: true),
        new("DS7", "A link detached without closing and without an error keeps its unsettled deliveries live: a disposition that names one is applied in both roles, so a pending send completes with the receiver's outcome and the application can still settle a delivery it took. They end, failing with link-detached, when the link closes or detaches with an error, when it attaches again under its name (they carry over to the new attachment's unsettled maps), or when the session ends. A delivery whose frames were not all sent cannot complete on that attachment and ends at once, and a disposition that breaks a rule for a suspended link's delivery is not applied, since the link has no handle left to detach.", "Part 2, sections 2.6.13 and 2.7.6",
            Tests:
            [
                nameof(SendAsync_OnSuspendedLink_ShouldCompleteWithTheOutcomeSentLater),
                nameof(AcceptAsync_OnSuspendedLink_ShouldSettleTheDelivery),
                nameof(OnDisposition_OnSuspendedReceiver_ShouldApplyTheSendersState),
                nameof(SendAsync_OnLinkThatEndsItsDeliveries_ShouldFailWithLinkDetached),
                nameof(AcceptAsync_OnSuspendedLinkThatEnds_ShouldFailWithLinkDetached),
                nameof(SendAsync_OnLinkSuspendedMidDelivery_ShouldFailAtOnce)
            ],
            Refusals: [nameof(OnDisposition_BreakingARuleForSuspendedLinkDelivery_ShouldNotApplyIt)])
    ];

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should refuse a delivery whose settled flag contradicts its link's sender settle mode before sending it")]
    [InlineData(AmqpSenderSettleMode.Settled, false)]
    [InlineData(AmqpSenderSettleMode.Unsettled, true)]
    public async Task SendAsync_WithSettledFlagAgainstSenderSettleMode_ShouldThrow(AmqpSenderSettleMode mode, bool settled)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession clientSession, IAmqpSession serverSession) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        AmqpSenderOptions options = new()
        {
            Name = "settle-mode",
            Target = new AmqpTarget { Address = "queue" },
            SenderSettleMode = mode
        };
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(clientSession, serverSession, options, cancellationToken: timeout.Token);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            async () => await sender.SendAsync(new byte[] { 1 }, new AmqpDeliveryOptions { Settled = settled }, timeout.Token));
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[] { 2 }, cancellationToken: timeout.Token).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        bool arrivedSettled = delivery.IsSettled;

        if (!arrivedSettled)
        {
            await delivery.AcceptAsync(timeout.Token);
        }

        await send;

        // Assert
        exception.ParamName.ShouldBe("options");
        exception.Message.ShouldStartWith(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DeliverySettleModeConflict, mode), Case.Sensitive);
        delivery.Payload.ToArray().ShouldBe(new byte[] { 2 });
        arrivedSettled.ShouldBe(mode == AmqpSenderSettleMode.Settled);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link with invalid-field when a transfer asks for receiver settle mode second on a link that settles first")]
    public async Task OnTransfer_WithReceiverSettleModeSecondOnFirstLink_ShouldDetachWithInvalidField()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);
        AmqpTransferPerformative transfer = CreateTransfer(deliveryId: 0);
        transfer.ReceiverSettleMode = AmqpReceiverSettleMode.Second;

        // Act
        await peer.WriteFrameAsync(0, transfer, timeout.Token, payload: new byte[] { 1 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        receiver.ReceiverSettleMode.ShouldBe(AmqpReceiverSettleMode.First);
        detach.Closed.ShouldBeTrue();
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        detach.Error.Description.ShouldBe(ErrorMessages.TransferSettleModeInvalid);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDisposition: Should settle only the deliveries it holds unsettled within a range that reaches past them")]
    public async Task OnDisposition_OnRangeBeyondUnsettledDeliveries_ShouldSettleOnlyThoseItHolds()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateLinkFlow(engineBegin, credit: 10), timeout.Token);
        Task<AmqpDeliveryState?> first = sender.SendAsync(new byte[] { 1 }, cancellationToken: timeout.Token).AsTask();
        uint firstId = (await ReadDeliveryAsync(peer, timeout.Token)).Single().DeliveryId!.Value;
        Task<AmqpDeliveryState?> second = sender.SendAsync(new byte[] { 2 }, cancellationToken: timeout.Token).AsTask();
        uint secondId = (await ReadDeliveryAsync(peer, timeout.Token)).Single().DeliveryId!.Value;

        // Act: the range reaches eight delivery-ids past the last one the engine sent.
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative
        {
            Role = AmqpRole.Receiver,
            First = firstId,
            Last = unchecked(secondId + 8),
            Settled = true,
            State = AmqpAccepted.Instance
        }, timeout.Token);
        AmqpDeliveryState? firstOutcome = await first.WaitAsync(timeout.Token);
        AmqpDeliveryState? secondOutcome = await second.WaitAsync(timeout.Token);
        Task<AmqpDeliveryState?> third = sender.SendAsync(new byte[] { 3 }, cancellationToken: timeout.Token).AsTask();
        uint thirdId = (await ReadDeliveryAsync(peer, timeout.Token)).Single().DeliveryId!.Value;
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative
        {
            Role = AmqpRole.Receiver,
            First = thirdId,
            Settled = true,
            State = AmqpReleased.Instance
        }, timeout.Token);
        AmqpDeliveryState? thirdOutcome = await third.WaitAsync(timeout.Token);

        // Assert
        secondId.ShouldBe(unchecked(firstId + 1));
        firstOutcome.ShouldBeOfType<AmqpAccepted>();
        secondOutcome.ShouldBeOfType<AmqpAccepted>();
        thirdId.ShouldBe(unchecked(secondId + 1));
        thirdOutcome.ShouldBeOfType<AmqpReleased>();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should take a transfer's settled flag even where it contradicts the peer's sender settle mode")]
    [InlineData(AmqpSenderSettleMode.Unsettled, true)]
    [InlineData(AmqpSenderSettleMode.Settled, false)]
    public async Task OnTransfer_OnSettledFlagAgainstSenderSettleMode_ShouldTakeTheFlag(AmqpSenderSettleMode mode, bool settled)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        AmqpAttachPerformative attach = CreateSenderAttach(new AmqpTarget { Address = "queue" });
        attach.SenderSettleMode = mode;
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, attach, null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, settled: settled), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        receiver.SenderSettleMode.ShouldBe(mode);
        delivery.IsSettled.ShouldBe(settled);
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should complete with the outcome the receiver sends after the link is suspended")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SendAsync_OnSuspendedLink_ShouldCompleteWithTheOutcomeSentLater(bool receiverSettles)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateLinkFlow(engineBegin, credit: 1), timeout.Token);
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[] { 1 }, cancellationToken: timeout.Token).AsTask();
        uint deliveryId = (await ReadDeliveryAsync(peer, timeout.Token)).Single().DeliveryId!.Value;
        await SuspendAsync(sender, peer, null, timeout.Token);
        bool pendingWhileSuspended = !send.IsCompleted;

        // Act: under receiver settle mode second the receiver states the outcome without settling.
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative
        {
            Role = AmqpRole.Receiver,
            First = deliveryId,
            Settled = receiverSettles,
            State = AmqpAccepted.Instance
        }, timeout.Token);
        AmqpDeliveryState? outcome = await send.WaitAsync(timeout.Token);
        AmqpPerformative next = await ReadNextAfterEchoAsync(peer, timeout.Token);

        // Assert
        pendingWhileSuspended.ShouldBeTrue();
        sender.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        outcome.ShouldBeOfType<AmqpAccepted>();

        if (receiverSettles)
        {
            next.ShouldBeOfType<AmqpFlowPerformative>();
        }
        else
        {
            AmqpDispositionPerformative settlement = next.ShouldBeOfType<AmqpDispositionPerformative>();
            settlement.Role.ShouldBe(AmqpRole.Sender);
            settlement.First.ShouldBe(deliveryId);
            settlement.Settled.ShouldBeTrue();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should settle a delivery the application took before the link was suspended")]
    [InlineData(AmqpReceiverSettleMode.First)]
    [InlineData(AmqpReceiverSettleMode.Second)]
    public async Task AcceptAsync_OnSuspendedLink_ShouldSettleTheDelivery(AmqpReceiverSettleMode mode)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(
            peer,
            session,
            new AmqpTarget { Address = "queue" },
            new AmqpReceiverOptions { ReceiverSettleMode = mode },
            timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await SuspendAsync(receiver, peer, null, timeout.Token);

        // Act: under receiver settle mode second the sender settles after the outcome.
        Task accept = delivery.AcceptAsync(timeout.Token).AsTask();
        AmqpDispositionPerformative disposition = await ReadUntilAsync<AmqpDispositionPerformative>(peer, timeout.Token);
        bool settledBeforeSender = delivery.IsSettled;

        if (mode == AmqpReceiverSettleMode.Second)
        {
            await peer.WriteFrameAsync(0, new AmqpDispositionPerformative { Role = AmqpRole.Sender, First = 0, Settled = true }, timeout.Token);
        }

        await accept.WaitAsync(timeout.Token);

        // Assert
        receiver.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        disposition.Role.ShouldBe(AmqpRole.Receiver);
        disposition.First.ShouldBe(0u);
        disposition.Settled.ShouldBe(mode == AmqpReceiverSettleMode.First);
        disposition.State.ShouldBeOfType<AmqpAccepted>();
        settledBeforeSender.ShouldBe(mode == AmqpReceiverSettleMode.First);
        delivery.IsSettled.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDisposition: Should apply the state and settlement a sender sends for a delivery of a suspended link")]
    public async Task OnDisposition_OnSuspendedReceiver_ShouldApplyTheSendersState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await SuspendAsync(receiver, peer, null, timeout.Token);

        // Act: what the delivery reports is what the application lists when it attaches the link again.
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative
        {
            Role = AmqpRole.Sender,
            First = 0,
            Settled = true,
            State = AmqpReleased.Instance
        }, timeout.Token);
        await SyncAsync(peer, timeout.Token);
        await delivery.AcceptAsync(timeout.Token);
        AmqpPerformative next = await ReadNextAfterEchoAsync(peer, timeout.Token);

        // Assert
        delivery.IsSettled.ShouldBeTrue();
        delivery.RemoteState.ShouldBeOfType<AmqpReleased>();
        next.ShouldBeOfType<AmqpFlowPerformative>();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should fail a pending send once the link closes, detaches with an error, attaches again or loses its session")]
    [InlineData("closed by the peer")]
    [InlineData("detached by the peer with an error")]
    [InlineData("suspension answered with an error")]
    [InlineData("attached again")]
    [InlineData("session ended")]
    public async Task SendAsync_OnLinkThatEndsItsDeliveries_ShouldFailWithLinkDetached(string end)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateLinkFlow(engineBegin, credit: 1), timeout.Token);
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[] { 1 }, cancellationToken: timeout.Token).AsTask();
        await ReadDeliveryAsync(peer, timeout.Token);
        AmqpError error = new(AmqpErrorCondition.InternalError, "The peer failed.");

        // Act
        switch (end)
        {
            case "closed by the peer":
                await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = true }, timeout.Token);
                break;
            case "detached by the peer with an error":
                await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = false, Error = error }, timeout.Token);
                break;
            case "suspension answered with an error":
                await SuspendAsync(sender, peer, error, timeout.Token);
                break;
            case "attached again":
                await SuspendAsync(sender, peer, null, timeout.Token);
                await peer.WriteFrameAsync(0, CreateReceiverAttach(new AmqpTarget { Address = "peer" }), timeout.Token);
                await session.AcceptLinkAsync(timeout.Token);
                break;
            case "session ended":
                await SuspendAsync(sender, peer, null, timeout.Token);
                await peer.WriteFrameAsync(0, new AmqpEndPerformative(), timeout.Token);
                await ReadUntilAsync<AmqpEndPerformative>(peer, timeout.Token);
                break;
        }

        AmqpException exception = await Should.ThrowAsync<AmqpException>(send.WaitAsync(timeout.Token));

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);

        switch (end)
        {
            case "closed by the peer":
                exception.Error.ShouldBeNull();
                break;
            case "detached by the peer with an error":
            case "suspension answered with an error":
                exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InternalError);
                break;
            case "attached again":
                exception.Message.ShouldBe(ErrorMessages.DeliveryCarriedOver);
                break;
            case "session ended":
                exception.InnerException.ShouldBeOfType<AmqpException>().ErrorCode.ShouldBe(AmqpErrorCode.SessionEnded);
                break;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should fail to settle a delivery of a suspended link once the link attaches again or loses its session")]
    [InlineData("attached again")]
    [InlineData("session ended")]
    public async Task AcceptAsync_OnSuspendedLinkThatEnds_ShouldFailWithLinkDetached(string end)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await SuspendAsync(receiver, peer, null, timeout.Token);

        // Act
        if (end == "attached again")
        {
            await peer.WriteFrameAsync(0, CreateSenderAttach(new AmqpTarget { Address = "queue" }), timeout.Token);
            await session.AcceptLinkAsync(timeout.Token);
        }
        else
        {
            await peer.WriteFrameAsync(0, new AmqpEndPerformative(), timeout.Token);
            await ReadUntilAsync<AmqpEndPerformative>(peer, timeout.Token);
        }

        AmqpException exception = await Should.ThrowAsync<AmqpException>(delivery.AcceptAsync(timeout.Token).AsTask());

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        delivery.IsSettled.ShouldBeFalse();

        if (end == "attached again")
        {
            exception.Message.ShouldBe(ErrorMessages.DeliveryCarriedOver);
        }
        else
        {
            exception.InnerException.ShouldBeOfType<AmqpException>().ErrorCode.ShouldBe(AmqpErrorCode.SessionEnded);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should fail at once a delivery whose frames were not all sent when the link was suspended")]
    public async Task SendAsync_OnLinkSuspendedMidDelivery_ShouldFailAtOnce()
    {
        // Arrange: frames of at most 512 bytes and a window of one frame stop the delivery after its first frame.
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(null, AmqpTestPeer.CreateOpen(maxFrameSize: 512), timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateLinkFlow(engineBegin, credit: 1, incomingWindow: 1), timeout.Token);
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[2048], cancellationToken: timeout.Token).AsTask();
        AmqpTransferPerformative first = await ReadUntilAsync<AmqpTransferPerformative>(peer, timeout.Token);

        // Act: an outcome naming the delivery afterwards finds nothing to settle.
        await SuspendAsync(sender, peer, null, timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(send.WaitAsync(timeout.Token));
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative
        {
            Role = AmqpRole.Receiver,
            First = first.DeliveryId!.Value,
            Settled = false,
            State = AmqpAccepted.Instance
        }, timeout.Token);
        AmqpPerformative next = await ReadNextAfterEchoAsync(peer, timeout.Token);

        // Assert
        first.More.ShouldBeTrue();
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        exception.Error.ShouldBeNull();
        next.ShouldBeOfType<AmqpFlowPerformative>();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDisposition: Should not apply a disposition that breaks a rule for a delivery of a suspended link")]
    public async Task OnDisposition_BreakingARuleForSuspendedLinkDelivery_ShouldNotApplyIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await SuspendAsync(receiver, peer, null, timeout.Token);

        // Act: only the first frame of a resumed delivery carries the received state, never a sender's disposition.
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative
        {
            Role = AmqpRole.Sender,
            First = 0,
            Settled = true,
            State = new AmqpReceived(0, 0)
        }, timeout.Token);
        AmqpPerformative next = await ReadNextAfterEchoAsync(peer, timeout.Token);

        // Assert
        next.ShouldBeOfType<AmqpFlowPerformative>();
        delivery.IsSettled.ShouldBeFalse();
        delivery.RemoteState.ShouldBeNull();
        session.SessionEnded.IsCancellationRequested.ShouldBeFalse();
    }

    /// <summary>
    /// Suspends a link the peer attached on handle 0: the engine detaches it without closing, and the peer answers
    /// in kind, or with an error.
    /// </summary>
    private static async Task SuspendAsync(IAmqpLink link, AmqpTestPeer peer, AmqpError? answerError, CancellationToken cancellationToken)
    {
        Task detach = link.DetachAsync(cancellationToken: cancellationToken).AsTask();
        AmqpDetachPerformative engineDetach = await ReadUntilAsync<AmqpDetachPerformative>(peer, cancellationToken);

        engineDetach.Closed.ShouldBeFalse();
        engineDetach.Error.ShouldBeNull();
        await peer.WriteFrameAsync(0, new AmqpDetachPerformative { Handle = 0, Closed = false, Error = answerError }, cancellationToken);
        await detach;
    }
}
