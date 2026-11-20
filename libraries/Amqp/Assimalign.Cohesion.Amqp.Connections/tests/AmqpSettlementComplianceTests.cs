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
            Leniency: true)
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
}
