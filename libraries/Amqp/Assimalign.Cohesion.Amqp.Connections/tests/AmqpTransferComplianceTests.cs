using System;
using System.Collections.Generic;
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
/// Drives the engine's links with a scripted peer through the rules of AMQP 1.0 Part 2 for moving one delivery:
/// splitting it into <c>transfer</c> frames and joining them, aborting it, settling it across its frames, holding
/// the sender's terminal outcome, and the credit it spends, including a drain's. Each rule in <see cref="Rules"/>
/// names the tests that pin it.
/// </summary>
public class AmqpTransferComplianceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    public static TheoryData<string> DeliveryFields => new() { "delivery-id", "delivery-tag" };

    public static TheoryData<string> ChangedFields => new() { "delivery-id", "delivery-tag", "message-format" };

    /// <summary>
    /// Gets the Part 2 rules for moving a delivery that the engine holds, each with the tests that pin it.
    /// </summary>
    internal static IReadOnlyList<AmqpTransferRule> Rules { get; } =
    [
        new("F1", "A delivery too large for one frame is split: every frame but the last sets more, only the first names the delivery-id, delivery-tag, message format and settlement, and no frame exceeds the frame size.", "Part 2, sections 2.6.14 and 2.7.5", Forbids: false,
        [
            nameof(SendAsync_OnDeliveryLargerThanFrame_ShouldSplitIt),
            nameof(AmqpConnectionTests.SendAsync_OnMessageLargerThanFrame_ShouldReassembleDelivery)
        ]),
        new("F2", "A receiver joins a delivery's frames in the order they arrive, and the last frame may carry no payload.", "Part 2, section 2.6.14", Forbids: false,
        [
            nameof(OnTransfer_OnSplitDelivery_ShouldJoinFramesInOrder),
            nameof(OnTransfer_OnEmptyLastFrame_ShouldCompleteDelivery)
        ]),
        new("F3", "The first frame of a delivery names its delivery-id and delivery-tag: a receiver detaches a link whose first frame omits either with amqp:invalid-field.", "Part 2, section 2.7.5", Forbids: true,
        [
            nameof(OnTransfer_OnFirstFrameNotNamingDelivery_ShouldDetachWithInvalidField)
        ]),
        new("F4", "A later frame may repeat the delivery-id, delivery-tag and message format, and must not change any of them: a receiver detaches a link whose later frame does with amqp:invalid-field.", "Part 2, section 2.7.5", Forbids: true,
        [
            nameof(OnTransfer_OnContinuationRepeatingDelivery_ShouldAcceptIt),
            nameof(OnTransfer_OnContinuationChangingDelivery_ShouldDetachWithInvalidField)
        ]),
        new("F5", "Deliveries on one link are not interleaved: a receiver detaches a link whose sender starts a delivery before the last frame of the one in progress with amqp:invalid-field.", "Part 2, section 2.6.14", Forbids: true,
        [
            nameof(OnTransfer_OnDeliveryInterleavedWithAnother_ShouldDetachWithInvalidField)
        ]),
        new("F6", "Each delivery takes one unit of link credit, with its first frame: a receiver detaches a link whose sender starts a delivery without credit with amqp:transfer-limit-exceeded.", "Part 2, section 2.6.7", Forbids: true,
        [
            nameof(OnTransfer_OnSplitDelivery_ShouldTakeOneUnitOfCredit),
            nameof(AmqpEngineViolationTests.OnTransfer_WithoutCredit_ShouldDetachWithTransferLimitExceeded)
        ]),
        new("F7", "A receiver measures a delivery across its frames against its max-message-size, and detaches the link with amqp:message-size-exceeded once the delivery grows past it.", "Part 2, section 2.7.3", Forbids: true,
        [
            nameof(OnTransfer_OnSplitDeliveryAboveMaxMessageSize_ShouldDetachWithMessageSizeExceeded),
            nameof(AmqpEngineViolationTests.OnTransfer_AboveMaxMessageSize_ShouldDetachWithMessageSizeExceeded)
        ]),
        new("F8", "An aborted delivery is discarded and settled implicitly: the aborting frame's payload is ignored, nothing reaches the application or stays buffered, the delivery's credit stays spent, and the link keeps receiving.", "Part 2, sections 2.6.14 and 2.7.5", Forbids: false,
        [
            nameof(OnTransfer_OnAbortedDelivery_ShouldDiscardItAndKeepReceiving),
            nameof(OnTransfer_OnAbortedFirstFrame_ShouldDiscardIt)
        ]),
        new("F9", "The aborted flag takes precedence over more: a receiver ignores more on an aborting frame.", "Part 2, section 2.7.5", Forbids: false,
        [
            nameof(OnTransfer_OnAbortedFrameWithMore_ShouldDiscardDelivery)
        ]),
        new("F10", "A sender that stops a delivery after its first frame aborts it with one frame that sets aborted and not more.", "Part 2, sections 2.6.14 and 2.7.5", Forbids: false,
        [
            nameof(SendAsync_OnCancelAfterFirstFrame_ShouldAbortDelivery),
            nameof(AmqpTransactionComplianceTests.SendAsync_OnCanceledTransactionalDelivery_ShouldNameTransactionOnAbort)
        ]),
        new("F11", "Settlement on a frame holds for the rest of the delivery: a later frame that leaves settled unset keeps a settled delivery settled, and a later frame may settle a delivery its first frame left unsettled.", "Part 2, section 2.7.5", Forbids: false,
        [
            nameof(OnTransfer_OnContinuationLeavingSettledUnset_ShouldKeepDeliverySettled),
            nameof(OnTransfer_OnLastFrameSettled_ShouldSettleDelivery)
        ]),
        new("F12", "A later frame that clears settled leaves a settled delivery settled: a delivery cannot become unsettled again, so the engine ignores the flag.", "Part 2, section 2.7.5", Forbids: false,
        [
            nameof(OnTransfer_OnContinuationClearingSettled_ShouldKeepDeliverySettled)
        ], Leniency: true),
        new("F13", "A first frame without a message format is read as format 0, the AMQP message format, although the first frame of a split delivery must name it: the meaning is clear.", "Part 2, section 2.7.5", Forbids: false,
        [
            nameof(OnTransfer_OnFirstFrameWithoutMessageFormat_ShouldReadFormatZero)
        ], Leniency: true),
        new("F14", "A transfer cannot ask for receiver settle mode second on a link whose receiver settles first, which a receiver refuses with amqp:invalid-field, and one that asks for first on a link that settles second is settled first.", "Part 2, section 2.7.5", Forbids: true,
        [
            nameof(OnTransfer_OnSecondSettleModeOnFirstModeLink_ShouldDetachWithInvalidField),
            nameof(OnTransfer_OnFirstSettleModeOnSecondModeLink_ShouldSettleFirst)
        ]),
        new("F15", "A delivery-id names one unsettled delivery of its session: a session whose peer starts a delivery under the id of an unsettled one ends with amqp:illegal-state.", "Part 2, section 2.6", Forbids: true,
        [
            nameof(OnTransfer_OnDeliveryIdOfUnsettledDelivery_ShouldEndSessionWithIllegalState)
        ]),
        new("F16", "A terminal outcome the sender stated for a delivery does not change: a receiver detaches a link whose sender states another outcome on a later frame or disposition with amqp:illegal-state. Outcomes compare by kind.", "Part 2, section 2.7.5", Forbids: true,
        [
            nameof(OnTransfer_OnContinuationChangingOutcome_ShouldDetachWithIllegalState),
            nameof(OnTransfer_OnContinuationRestatingOutcome_ShouldDeliverIt),
            nameof(OnDisposition_OnSenderChangingOutcome_ShouldDetachWithIllegalState),
            nameof(OnDisposition_OnSenderRestatingOutcome_ShouldSettleDelivery),
            nameof(OnDisposition_OnSenderSettlingWithoutState_ShouldKeepOutcome),
            nameof(OnDisposition_OnChangeAfterDispositionWithoutState_ShouldDetachWithIllegalState)
        ]),
        new("F17", "A receiver does not check that a new delivery's tag differs from those of its unsettled deliveries: deliveries settle by delivery-id, so a repeated tag puts nothing at risk until the link is resumed, and the application builds the unsettled map it resumes with.", "Part 2, section 2.6.12", Forbids: false,
        [
            nameof(OnTransfer_OnTagOfUnsettledDelivery_ShouldAcceptIt)
        ], Leniency: true),
        new("F18", "A sender that drains uses up the credit it cannot use: it advances its delivery-count by that credit, sets its link-credit to zero, and sends a flow whose drain field keeps the receiver's last value, since only the receiver changes it.", "Part 2, sections 2.6.7 and 2.7.4", Forbids: false,
        [
            nameof(OnFlow_OnDrainWithNothingToSend_ShouldAdvanceDeliveryCountAndKeepDrain),
            nameof(AmqpConnectionTests.DrainAsync_OnIdleSender_ShouldReturnCredit)
        ]),
        new("F19", "A receiver's drain ends once the sender's delivery-count reaches the delivery limit: a flow that reports no credit without advancing the delivery-count does not end it, because a flow the sender wrote before it read the drain reads the same way.", "Part 2, section 2.6.7", Forbids: false,
        [
            nameof(DrainAsync_OnSenderReportingNoCreditWithoutAdvancing_ShouldKeepDraining),
            nameof(AmqpConnectionTests.DrainAsync_OnIdleSender_ShouldReturnCredit)
        ]),
        new("F20", "A receiver ignores the drain field of the sender's flow, which should repeat the receiver's own: some libraries clear it in the flow that ends a drain, and the delivery-count already says the drain ended.", "Part 2, sections 2.6.7 and 2.7.4", Forbids: false,
        [
            nameof(DrainAsync_OnSenderUsingUpCreditWithDrainCleared_ShouldEnd)
        ], Leniency: true)
    ];

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Rules: Should name at least one test for every Part 2 transfer rule, with unique identifiers")]
    public void Rules_OnTransferMap_ShouldNameTestsForEveryRule()
    {
        // Arrange
        IReadOnlyList<AmqpTransferRule> rules = Rules;

        // Act
        string[] identifiers = [.. rules.Select(rule => rule.Id)];

        // Assert
        identifiers.Distinct().Count().ShouldBe(identifiers.Length);
        rules.ShouldAllBe(rule => rule.Tests.Count > 0 && rule.Reference.StartsWith("Part 2", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should split a delivery larger than a frame, naming it on the first frame only")]
    public async Task SendAsync_OnDeliveryLargerThanFrame_ShouldSplitIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(null, AmqpTestPeer.CreateOpen(maxFrameSize: 512), timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateLinkFlow(engineBegin, credit: 1), timeout.Token);
        byte[] payload = [.. Enumerable.Range(0, 1500).Select(i => (byte)(i % 251))];

        // Act
        Task<AmqpDeliveryState?> send = sender.SendAsync(payload, new AmqpDeliveryOptions { Settled = true }, timeout.Token).AsTask();
        List<AmqpTestFrame> frames = await ReadDeliveryFramesAsync(peer, timeout.Token);
        await send;

        // Assert
        AmqpTransferPerformative[] transfers = [.. frames.Select(frame => (AmqpTransferPerformative)frame.Performative!)];
        transfers.Length.ShouldBeGreaterThan(2);
        transfers[0].DeliveryId.ShouldNotBeNull();
        transfers[0].DeliveryTag.ShouldNotBeNull();
        transfers[0].MessageFormat.ShouldBe(0u);
        transfers[0].Settled.ShouldBe(true);
        transfers.Skip(1).ShouldAllBe(transfer => transfer.DeliveryId == null && transfer.DeliveryTag == null && transfer.MessageFormat == null && transfer.Settled == null);
        transfers[..^1].ShouldAllBe(transfer => transfer.More);
        transfers[^1].More.ShouldBeFalse();
        frames.ShouldAllBe(frame => frame.Header.Size <= 512);
        frames.SelectMany(frame => frame.Payload).ToArray().ShouldBe(payload);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should join the frames of a split delivery in the order they arrive")]
    public async Task OnTransfer_OnSplitDelivery_ShouldJoinFramesInOrder()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0)), timeout.Token, payload: new byte[] { 1, 2 });
        await peer.WriteFrameAsync(0, Continuation(more: true), timeout.Token, payload: new byte[] { 3 });
        await peer.WriteFrameAsync(0, Continuation(), timeout.Token, payload: new byte[] { 4, 5, 6 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.DeliveryTag.ShouldBe(AmqpDeliveryTag.Create(0UL));
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1, 2, 3, 4, 5, 6 });
        delivery.IsSettled.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should complete a delivery whose last frame carries no payload")]
    public async Task OnTransfer_OnEmptyLastFrame_ShouldCompleteDelivery()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0)), timeout.Token, payload: new byte[] { 1, 2, 3 });
        await peer.WriteFrameAsync(0, Continuation(), timeout.Token);
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose first frame does not name the delivery with amqp:invalid-field")]
    [MemberData(nameof(DeliveryFields))]
    public async Task OnTransfer_OnFirstFrameNotNamingDelivery_ShouldDetachWithInvalidField(string missing)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        AmqpTransferPerformative transfer = CreateTransfer(deliveryId: 0);

        if (missing == "delivery-id")
        {
            transfer.DeliveryId = null;
        }
        else
        {
            transfer.DeliveryTag = null;
        }

        // Act
        await peer.WriteFrameAsync(0, transfer, timeout.Token, payload: new byte[] { 1 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should accept a later frame that repeats the delivery-id, delivery-tag and message format")]
    public async Task OnTransfer_OnContinuationRepeatingDelivery_ShouldAcceptIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        AmqpTransferPerformative repeated = CreateTransfer(deliveryId: 0);

        // Act
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0)), timeout.Token, payload: new byte[] { 1 });
        await peer.WriteFrameAsync(0, repeated, timeout.Token, payload: new byte[] { 2 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1, 2 });
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose later frame changes the delivery-id, delivery-tag or message format with amqp:invalid-field")]
    [MemberData(nameof(ChangedFields))]
    public async Task OnTransfer_OnContinuationChangingDelivery_ShouldDetachWithInvalidField(string field)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        AmqpTransferPerformative changed = Continuation();

        switch (field)
        {
            case "delivery-id":
                changed.DeliveryId = 1;
                break;
            case "delivery-tag":
                changed.DeliveryTag = AmqpDeliveryTag.Create(9UL);
                break;
            default:
                changed.MessageFormat = 1;
                break;
        }

        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0)), timeout.Token, payload: new byte[] { 1 });

        // Act
        await peer.WriteFrameAsync(0, changed, timeout.Token, payload: new byte[] { 2 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose sender starts a delivery inside another with amqp:invalid-field")]
    public async Task OnTransfer_OnDeliveryInterleavedWithAnother_ShouldDetachWithInvalidField()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0)), timeout.Token, payload: new byte[] { 1 });

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 1), timeout.Token, payload: new byte[] { 2 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should take one unit of credit for a split delivery")]
    public async Task OnTransfer_OnSplitDelivery_ShouldTakeOneUnitOfCredit()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, new AmqpReceiverOptions { Prefetch = 0 }, timeout.Token);
        receiver.AddCredit(1);

        // Act
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0)), timeout.Token, payload: new byte[] { 1 });
        await peer.WriteFrameAsync(0, Continuation(more: true), timeout.Token, payload: new byte[] { 2 });
        await peer.WriteFrameAsync(0, Continuation(), timeout.Token, payload: new byte[] { 3 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 1), timeout.Token, payload: new byte[] { 4 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.TransferLimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose split delivery grows past max-message-size with amqp:message-size-exceeded")]
    public async Task OnTransfer_OnSplitDeliveryAboveMaxMessageSize_ShouldDetachWithMessageSizeExceeded()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, new AmqpReceiverOptions { MaxMessageSize = 16 }, timeout.Token);
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0)), timeout.Token, payload: new byte[10]);

        // Act
        await peer.WriteFrameAsync(0, Continuation(), timeout.Token, payload: new byte[10]);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.MessageSizeExceeded);
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should discard an aborted delivery, spend its credit and keep receiving")]
    public async Task OnTransfer_OnAbortedDelivery_ShouldDiscardItAndKeepReceiving()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, new AmqpReceiverOptions { Prefetch = 0 }, timeout.Token);
        receiver.AddCredit(2);
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0)), timeout.Token, payload: new byte[] { 1, 2 });

        // Act
        await peer.WriteFrameAsync(0, new AmqpTransferPerformative { Handle = 0, Aborted = true }, timeout.Token, payload: new byte[] { 9, 9, 9 });
        AmqpDeliveryState? partial = receiver.PartialDeliveryState;
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 1), timeout.Token, payload: new byte[] { 3, 4 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        bool more = receiver.TryReceive(out _);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 2), timeout.Token, payload: new byte[] { 5 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        partial.ShouldBeNull();
        delivery.DeliveryTag.ShouldBe(AmqpDeliveryTag.Create(1UL));
        delivery.Payload.ToArray().ShouldBe(new byte[] { 3, 4 });
        delivery.IsAborted.ShouldBeFalse();
        more.ShouldBeFalse();
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.TransferLimitExceeded);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should discard a delivery whose only frame aborts it")]
    public async Task OnTransfer_OnAbortedFirstFrame_ShouldDiscardIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        AmqpTransferPerformative aborted = CreateTransfer(deliveryId: 0);
        aborted.Aborted = true;

        // Act
        await peer.WriteFrameAsync(0, aborted, timeout.Token, payload: new byte[] { 9 });
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 1), timeout.Token, payload: new byte[] { 3 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.DeliveryTag.ShouldBe(AmqpDeliveryTag.Create(1UL));
        delivery.Payload.ToArray().ShouldBe(new byte[] { 3 });
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should ignore more on a frame that aborts the delivery")]
    public async Task OnTransfer_OnAbortedFrameWithMore_ShouldDiscardDelivery()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0)), timeout.Token, payload: new byte[] { 1 });

        // Act
        await peer.WriteFrameAsync(0, new AmqpTransferPerformative { Handle = 0, More = true, Aborted = true }, timeout.Token, payload: new byte[] { 9 });
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 1), timeout.Token, payload: new byte[] { 3 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.DeliveryTag.ShouldBe(AmqpDeliveryTag.Create(1UL));
        delivery.Payload.ToArray().ShouldBe(new byte[] { 3 });
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should abort a delivery canceled after its first frame with one aborting frame")]
    public async Task SendAsync_OnCancelAfterFirstFrame_ShouldAbortDelivery()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(null, AmqpTestPeer.CreateOpen(maxFrameSize: 512), timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(AmqpTestPeer.CreateBegin(incomingWindow: 1), timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateLinkFlow(engineBegin, credit: 1, incomingWindow: 1), timeout.Token);
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[1500], null, cancel.Token).AsTask();
        AmqpTransferPerformative first = await ReadUntilAsync<AmqpTransferPerformative>(peer, timeout.Token);

        // Act
        cancel.Cancel();
        await peer.WriteFrameAsync(0, new AmqpFlowPerformative
        {
            NextIncomingId = unchecked(engineBegin.NextOutgoingId + 1),
            IncomingWindow = 10,
            NextOutgoingId = 1,
            OutgoingWindow = 100
        }, timeout.Token);
        AmqpTestFrame abort = await ReadFrameUntilAsync<AmqpTransferPerformative>(peer, timeout.Token);
        await Should.ThrowAsync<OperationCanceledException>(send);

        // Assert
        AmqpTransferPerformative transfer = (AmqpTransferPerformative)abort.Performative!;
        first.More.ShouldBeTrue();
        transfer.Aborted.ShouldBeTrue();
        transfer.More.ShouldBeFalse();
        transfer.DeliveryId.ShouldBeNull();
        abort.Payload.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should keep a delivery settled when a later frame leaves settled unset")]
    public async Task OnTransfer_OnContinuationLeavingSettledUnset_ShouldKeepDeliverySettled()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0, settled: true)), timeout.Token, payload: new byte[] { 1 });
        await peer.WriteFrameAsync(0, Continuation(), timeout.Token, payload: new byte[] { 2 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.IsSettled.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should settle a delivery whose last frame sets settled")]
    public async Task OnTransfer_OnLastFrameSettled_ShouldSettleDelivery()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        AmqpTransferPerformative last = Continuation();
        last.Settled = true;

        // Act
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0)), timeout.Token, payload: new byte[] { 1 });
        await peer.WriteFrameAsync(0, last, timeout.Token, payload: new byte[] { 2 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.IsSettled.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should keep a delivery settled when a later frame clears settled")]
    public async Task OnTransfer_OnContinuationClearingSettled_ShouldKeepDeliverySettled()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        AmqpTransferPerformative clearing = Continuation();
        clearing.Settled = false;

        // Act
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0, settled: true)), timeout.Token, payload: new byte[] { 1 });
        await peer.WriteFrameAsync(0, clearing, timeout.Token, payload: new byte[] { 2 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.IsSettled.ShouldBeTrue();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should read a first frame without a message format as format 0")]
    public async Task OnTransfer_OnFirstFrameWithoutMessageFormat_ShouldReadFormatZero()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        AmqpTransferPerformative transfer = First(CreateTransfer(deliveryId: 0));
        transfer.MessageFormat = null;

        // Act
        await peer.WriteFrameAsync(0, transfer, timeout.Token, payload: new byte[] { 1 });
        await peer.WriteFrameAsync(0, Continuation(), timeout.Token, payload: new byte[] { 2 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.MessageFormat.ShouldBe(0u);
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1, 2 });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link that settles first when a transfer asks to settle second with amqp:invalid-field")]
    public async Task OnTransfer_OnSecondSettleModeOnFirstModeLink_ShouldDetachWithInvalidField()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        AmqpTransferPerformative transfer = CreateTransfer(deliveryId: 0);
        transfer.ReceiverSettleMode = AmqpReceiverSettleMode.Second;

        // Act
        await peer.WriteFrameAsync(0, transfer, timeout.Token, payload: new byte[] { 1 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should settle first a delivery whose transfer asks for it on a link that settles second")]
    public async Task OnTransfer_OnFirstSettleModeOnSecondModeLink_ShouldSettleFirst()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, new AmqpReceiverOptions { ReceiverSettleMode = AmqpReceiverSettleMode.Second }, timeout.Token);
        AmqpTransferPerformative transfer = CreateTransfer(deliveryId: 0);
        transfer.ReceiverSettleMode = AmqpReceiverSettleMode.First;
        await peer.WriteFrameAsync(0, transfer, timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Act
        await delivery.AcceptAsync(timeout.Token);
        AmqpDispositionPerformative disposition = await ReadUntilAsync<AmqpDispositionPerformative>(peer, timeout.Token);

        // Assert
        delivery.IsSettled.ShouldBeTrue();
        disposition.Settled.ShouldBeTrue();
        disposition.State.ShouldBeOfType<AmqpAccepted>();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should end a session whose peer starts a delivery under the id of an unsettled one with amqp:illegal-state")]
    public async Task OnTransfer_OnDeliveryIdOfUnsettledDelivery_ShouldEndSessionWithIllegalState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, deliveryTag: AmqpDeliveryTag.Create(5UL)), timeout.Token, payload: new byte[] { 2 });
        AmqpEndPerformative end = await ReadUntilAsync<AmqpEndPerformative>(peer, timeout.Token);

        // Assert
        delivery.IsSettled.ShouldBeFalse();
        end.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose later frame changes the sender's terminal outcome with amqp:illegal-state")]
    public async Task OnTransfer_OnContinuationChangingOutcome_ShouldDetachWithIllegalState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        AmqpTransferPerformative changing = Continuation();
        changing.State = AmqpReleased.Instance;
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0, state: AmqpAccepted.Instance)), timeout.Token, payload: new byte[] { 1 });

        // Act
        await peer.WriteFrameAsync(0, changing, timeout.Token, payload: new byte[] { 2 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should accept a later frame that restates the sender's terminal outcome")]
    public async Task OnTransfer_OnContinuationRestatingOutcome_ShouldDeliverIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        AmqpRejected stated = new(new AmqpError(AmqpErrorCondition.NotFound, "gone"));
        AmqpTransferPerformative restating = Continuation();
        restating.State = new AmqpRejected();

        // Act
        await peer.WriteFrameAsync(0, First(CreateTransfer(deliveryId: 0, state: stated)), timeout.Token, payload: new byte[] { 1 });
        await peer.WriteFrameAsync(0, restating, timeout.Token, payload: new byte[] { 2 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        delivery.RemoteState.ShouldBeOfType<AmqpRejected>();
        delivery.Payload.ToArray().ShouldBe(new byte[] { 1, 2 });
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDisposition: Should detach a link whose sender's disposition changes its terminal outcome with amqp:illegal-state")]
    public async Task OnDisposition_OnSenderChangingOutcome_ShouldDetachWithIllegalState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, state: AmqpAccepted.Instance), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative { Role = AmqpRole.Sender, First = 0, State = AmqpReleased.Instance }, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
        delivery.RemoteState.ShouldBeOfType<AmqpAccepted>();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDisposition: Should settle a delivery whose sender's disposition restates its terminal outcome")]
    public async Task OnDisposition_OnSenderRestatingOutcome_ShouldSettleDelivery()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, state: AmqpAccepted.Instance), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative { Role = AmqpRole.Sender, First = 0, Settled = true, State = AmqpAccepted.Instance }, timeout.Token);
        await SyncAsync(peer, timeout.Token);

        // Assert
        delivery.IsSettled.ShouldBeTrue();
        delivery.RemoteState.ShouldBeOfType<AmqpAccepted>();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDisposition: Should keep the sender's terminal outcome when its settling disposition states none")]
    public async Task OnDisposition_OnSenderSettlingWithoutState_ShouldKeepOutcome()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, state: AmqpAccepted.Instance), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative { Role = AmqpRole.Sender, First = 0, Settled = true }, timeout.Token);
        await SyncAsync(peer, timeout.Token);

        // Assert
        delivery.IsSettled.ShouldBeTrue();
        delivery.RemoteState.ShouldBeOfType<AmqpAccepted>();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDisposition: Should detach a link whose sender changes its terminal outcome after a disposition that states none with amqp:illegal-state")]
    public async Task OnDisposition_OnChangeAfterDispositionWithoutState_ShouldDetachWithIllegalState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, state: AmqpAccepted.Instance), timeout.Token, payload: new byte[] { 1 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative { Role = AmqpRole.Sender, First = 0 }, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative { Role = AmqpRole.Sender, First = 0, State = AmqpReleased.Instance }, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
        delivery.RemoteState.ShouldBeOfType<AmqpAccepted>();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should accept a new delivery under the tag of an unsettled one")]
    public async Task OnTransfer_OnTagOfUnsettledDelivery_ShouldAcceptIt()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpReceiver receiver = await AttachReceiverAsync(host, null, timeout.Token);
        AmqpDeliveryTag tag = AmqpDeliveryTag.Create(7UL);

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, deliveryTag: tag), timeout.Token, payload: new byte[] { 1 });
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 1, deliveryTag: tag), timeout.Token, payload: new byte[] { 2 });
        IAmqpDelivery first = await receiver.ReceiveAsync(timeout.Token);
        IAmqpDelivery second = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        first.DeliveryTag.ShouldBe(tag);
        second.DeliveryTag.ShouldBe(tag);
        first.IsSettled.ShouldBeFalse();
        second.IsSettled.ShouldBeFalse();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnFlow: Should use up a drain's credit when nothing waits to send, keeping the receiver's drain flag")]
    public async Task OnFlow_OnDrainWithNothingToSend_ShouldAdvanceDeliveryCountAndKeepDrain()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);
        AmqpFlowPerformative drain = CreateLinkFlow(engineBegin, credit: 5);
        drain.Drain = true;

        // Act
        await peer.WriteFrameAsync(0, drain, timeout.Token);
        AmqpFlowPerformative answer = await ReadLinkFlowAsync(peer, timeout.Token);

        // Assert: rhea's receiver sees a drain end only in a flow that keeps the flag, and AMQPNetLite's only in one
        // that clears it. The specification decides (docs/DESIGN.md, "Interoperability").
        answer.DeliveryCount.ShouldBe(5u);
        answer.LinkCredit.ShouldBe(0u);
        answer.Drain.ShouldBeTrue();
        sender.Credit.ShouldBe(0u);
        sender.IsDraining.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DrainAsync: Should keep draining when the sender reports no credit without advancing its delivery-count")]
    public async Task DrainAsync_OnSenderReportingNoCreditWithoutAdvancing_ShouldKeepDraining()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        (IAmqpReceiver receiver, AmqpBeginPerformative engineBegin, Task drain, _) = await StartDrainAsync(host, timeout.Token);
        AmqpFlowPerformative stale = CreateSenderFlow(engineBegin, deliveryCount: 0, drain: false);
        stale.Echo = true;

        // Act: a flow the sender wrote before it read the drain reads like this, and so does AMQPNetLite's answer to a
        // drain that keeps the delivery limit. The echo carries the engine's state once it applied the flow.
        await host.Peer.WriteFrameAsync(0, stale, timeout.Token);
        AmqpFlowPerformative echo = await ReadLinkFlowAsync(host.Peer, timeout.Token);
        uint creditAfterStaleFlow = receiver.Credit;
        await host.Peer.WriteFrameAsync(0, CreateSenderFlow(engineBegin, deliveryCount: 5, drain: true), timeout.Token);
        await drain.WaitAsync(timeout.Token);

        // Assert
        echo.LinkCredit.ShouldBe(5u);
        echo.Drain.ShouldBeTrue();
        creditAfterStaleFlow.ShouldBe(5u);
        receiver.Credit.ShouldBe(0u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DrainAsync: Should end once the sender's delivery-count reaches the limit, though its flow clears the drain flag")]
    public async Task DrainAsync_OnSenderUsingUpCreditWithDrainCleared_ShouldEnd()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        (IAmqpReceiver receiver, AmqpBeginPerformative engineBegin, Task drain, AmqpFlowPerformative request) = await StartDrainAsync(host, timeout.Token);

        // Act: AMQPNetLite's sender clears the flag in the flow that ends a drain.
        await host.Peer.WriteFrameAsync(0, CreateSenderFlow(engineBegin, deliveryCount: 5, drain: false), timeout.Token);
        await drain.WaitAsync(timeout.Token);

        // Assert
        request.LinkCredit.ShouldBe(5u);
        receiver.Credit.ShouldBe(0u);
    }

    /// <summary>
    /// The peer attaches a sending link to a queue; the engine accepts it without prefetch, grants it five credits
    /// and asks it to drain. Returns the engine's <c>begin</c>, the wait for the drain to end and the <c>flow</c>
    /// that asked for it.
    /// </summary>
    private static async Task<(IAmqpReceiver Receiver, AmqpBeginPerformative EngineBegin, Task Drain, AmqpFlowPerformative Request)> StartDrainAsync(
        AmqpScriptedHost host,
        CancellationToken cancellationToken)
    {
        AmqpBeginPerformative engineBegin = await host.Peer.BeginAsync(null, cancellationToken);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(cancellationToken);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(host.Peer, session, new AmqpTarget { Address = "queue" }, new AmqpReceiverOptions { Prefetch = 0 }, cancellationToken);

        receiver.AddCredit(5);
        Task drain = receiver.DrainAsync(cancellationToken).AsTask();
        AmqpFlowPerformative request;

        do
        {
            request = await ReadLinkFlowAsync(host.Peer, cancellationToken);
        }
        while (!request.Drain);

        return (receiver, engineBegin, drain, request);
    }

    /// <summary>
    /// Creates a <c>flow</c> from the peer's sender on handle 0 that reports no credit left.
    /// </summary>
    private static AmqpFlowPerformative CreateSenderFlow(AmqpBeginPerformative engineBegin, uint deliveryCount, bool drain)
    {
        return new AmqpFlowPerformative
        {
            NextIncomingId = engineBegin.NextOutgoingId,
            IncomingWindow = 100,
            NextOutgoingId = 1,
            OutgoingWindow = 100,
            Handle = 0,
            DeliveryCount = deliveryCount,
            LinkCredit = 0,
            Drain = drain
        };
    }

    /// <summary>
    /// Reads frames until the engine sends a link's <c>flow</c>, skipping the session's.
    /// </summary>
    private static async Task<AmqpFlowPerformative> ReadLinkFlowAsync(AmqpTestPeer peer, CancellationToken cancellationToken)
    {
        while (true)
        {
            AmqpFlowPerformative flow = await ReadUntilAsync<AmqpFlowPerformative>(peer, cancellationToken);

            if (flow.Handle is not null)
            {
                return flow;
            }
        }
    }

    /// <summary>
    /// The peer begins a session and attaches a sending link to a queue; the engine accepts it as a receiver.
    /// </summary>
    private static async Task<IAmqpReceiver> AttachReceiverAsync(AmqpScriptedHost host, AmqpReceiverOptions? options, CancellationToken cancellationToken)
    {
        IAmqpSession session = await BeginAsync(host, cancellationToken);

        return await AttachPeerSenderAsync(host.Peer, session, new AmqpTarget { Address = "queue" }, options, cancellationToken);
    }
}
