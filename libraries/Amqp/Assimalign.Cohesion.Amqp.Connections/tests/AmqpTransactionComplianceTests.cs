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
/// Drives the engine's links with a scripted peer through the link-level rules of AMQP 1.0 Part 4
/// (transactions): the control link, one transaction per delivery, and transactional acquisition. Each rule
/// in <see cref="Rules"/> names the tests that pin it, here and in <see cref="AmqpTransactionCoordinatorTests"/>.
/// </summary>
public class AmqpTransactionComplianceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] _transactionA = [0x0A];
    private static readonly byte[] _transactionB = [0x0B];

    /// <summary>
    /// Gets the Part 4 rules the engine and its transaction helpers hold, each with the tests that pin it.
    /// </summary>
    internal static IReadOnlyList<AmqpComplianceRule> Rules { get; } =
    [
        new("T1", "A declare or discharge is never sent settled: the controller refuses to, and a coordinator detaches a link that does with amqp:illegal-state.", "Part 4, sections 4.2 and 4.3",
            Tests: [nameof(AmqpTransactionCoordinatorTests.AttachAsync_OnSession_ShouldAttachUnsettledFromSourceSupportingRejected)],
            Refusals:
            [
                nameof(OnTransfer_OnControlLinkSettled_ShouldDetachWithIllegalState),
                nameof(SendAsync_OnControlLink_ShouldRefuseSettledAndTransactionalDeliveries),
                nameof(AttachSenderAsync_ToCoordinatorSendingSettled_ShouldRefuse)
            ]),
        new("T2", "No transactional work is allowed on the control link: no transactional state on its transfers, on either end's dispositions, or on what the coordinator answers, and no transactional acquisition.", "Part 4, section 4.1",
            Tests: [nameof(AmqpTransactionCoordinatorTests.DeclareAsync_OnCoordinator_ShouldReturnDeclaredIdentifiers)],
            Refusals:
            [
                nameof(OnTransfer_OnControlLinkTransactional_ShouldDetachWithNotAllowed),
                nameof(OnDisposition_OnControlLinkTransactional_ShouldDetachWithNotAllowed),
                nameof(OnDisposition_OnTransactionalAnswerFromCoordinator_ShouldDetachWithNotAllowed),
                nameof(SendAsync_OnControlLink_ShouldRefuseSettledAndTransactionalDeliveries),
                nameof(SettleAsync_OnControlLinkWithTransactionalState_ShouldRefuse),
                nameof(SetAcquisitionTransaction_OnControlLink_ShouldRefuse)
            ]),
        new("T3", "A link to the coordinator cannot be resumed.", "Part 4, section 4.2",
            Tests: [nameof(AmqpTransactionCoordinatorTests.AttachAsync_OnSession_ShouldAttachUnsettledFromSourceSupportingRejected)],
            Refusals: [nameof(AttachSenderAsync_ToCoordinatorResuming_ShouldRefuse)]),
        new("T4", "Every frame of a split delivery belongs to the same transaction: the sender names it on each frame, an aborting one included, and a receiver detaches a link whose frames or later dispositions move a delivery into, out of, or between transactions with amqp:invalid-field.", "Part 4, section 4.4.1",
            Tests:
            [
                nameof(SendAsync_OnSplitTransactionalDelivery_ShouldNameTransactionOnEveryFrame),
                nameof(SendAsync_OnCanceledTransactionalDelivery_ShouldNameTransactionOnAbort)
            ],
            Refusals:
            [
                nameof(OnTransfer_OnContinuationInAnotherTransaction_ShouldDetachWithInvalidField),
                nameof(OnTransfer_OnContinuationOutOfTransaction_ShouldDetachWithInvalidField),
                nameof(OnTransfer_OnContinuationIntoTransaction_ShouldDetachWithInvalidField),
                nameof(OnDisposition_OnSenderMovingDelivery_ShouldDetachWithInvalidField)
            ]),
        new("T5", "A later frame without a state leaves the delivery in its transaction: the meaning is clear, so the engine accepts it.", "Part 4, section 4.4.1",
            Tests: [nameof(OnTransfer_OnContinuationWithoutState_ShouldKeepTransaction)],
            Refusals: [],
            Leniency: true),
        new("T6", "A resource can tell which transaction a partly posted delivery belongs to, so it can refuse a discharge against it.", "Part 4, section 4.4.1",
            Tests: [nameof(OnTransfer_OnContinuationWithoutState_ShouldKeepTransaction)],
            Refusals: []),
        new("T7", "A sender that does not support transactional acquisition terminates the link with amqp:not-implemented when a flow names a transaction, also when the flow arrives before the application answers the attach.", "Part 4, section 4.4.3",
            Tests: [nameof(AmqpTransactionCoordinatorTests.TransactionalAcquisition_OnSupportingSender_ShouldCarryTransactionToCommit)],
            Refusals:
            [
                nameof(OnFlow_WithTransactionIdWithoutSupport_ShouldDetachWithNotImplemented),
                nameof(OnFlow_WithTransactionIdBeforeAnswer_ShouldDetachWhenAnswered),
                nameof(AmqpTransactionCoordinatorTests.SetAcquisitionTransaction_OnSenderWithoutSupport_ShouldDetachWithNotImplemented)
            ]),
        new("T8", "A sender that supports it takes the transaction from the txn-id flow property, which is binary, and keeps it until the discharge clears it.", "Part 4, sections 4.4.3 and 4.5.4",
            Tests:
            [
                nameof(OnFlow_WithTransactionId_ShouldKeepItUntilCleared),
                nameof(AmqpTransactionCoordinatorTests.TransactionalAcquisition_OnSupportingSender_ShouldCarryTransactionToCommit)
            ],
            Refusals: [nameof(OnFlow_WithTransactionIdNotBinary_ShouldDetachWithInvalidField)]),
        new("T9", "A controller names the transaction it acquires under in the txn-id property of its flows.", "Part 4, section 4.4.3",
            Tests: [nameof(SetAcquisitionTransaction_OnReceiver_ShouldNameTransactionInEveryFlow)],
            Refusals: []),
        new("T10", "The coordinator answers a declare with declared and a discharge with accepted; it refuses one it cannot perform with a rejected outcome carrying the error when the controller's source supports rejected, and otherwise detaches the link with the error.", "Part 4, sections 4.2, 4.3 and 4.5.5",
            Tests:
            [
                nameof(AmqpTransactionCoordinatorTests.DeclareAsync_OnCoordinator_ShouldReturnDeclaredIdentifiers),
                nameof(AmqpTransactionCoordinatorTests.DischargeAsync_OnCommit_ShouldCommitAndEndTransaction),
                nameof(AmqpTransactionCoordinatorTests.DischargeAsync_OnRollback_ShouldRollBackAndEndTransaction),
                nameof(AmqpTransactionCoordinatorTests.DeclareAsync_OnHandlerRefusal_ShouldThrowWithTransactionError),
                nameof(AmqpTransactionCoordinatorTests.DischargeAsync_OnCommitRefusal_ShouldThrowAndEndTransaction),
                nameof(AmqpTransactionCoordinatorTests.DischargeAsync_OnUnknownTransaction_ShouldRefuseWithUnknownId),
                nameof(AmqpTransactionCoordinatorTests.RunAsync_OnSourceWithoutRejected_ShouldDetachWithError),
                nameof(AmqpTransactionCoordinatorTests.RunAsync_OnMessageThatIsNotControl_ShouldRejectWithNotImplemented),
                nameof(AmqpTransactionCoordinatorTests.RunAsync_OnUndecodablePayload_ShouldRejectWithDecodeError)
            ],
            Refusals: []),
        new("T11", "When the control link ends, every transaction it declared and did not discharge rolls back, also when one of the rollbacks fails.", "Part 4, section 4.1",
            Tests:
            [
                nameof(AmqpTransactionCoordinatorTests.DisposeAsync_OnControllerWithLiveTransactions_ShouldRollThemBack),
                nameof(AmqpTransactionCoordinatorTests.RunAsync_OnCancellation_ShouldCloseLinkAndRollBack),
                nameof(AmqpTransactionCoordinatorTests.RunAsync_OnHandlerFault_ShouldCloseWithInternalErrorAndRollBack),
                nameof(AmqpTransactionCoordinatorTests.RunAsync_OnFailedRollback_ShouldRollBackTheOthersAndThrow)
            ],
            Refusals: []),
        new("T12", "A transaction identifier is at most 32 octets and names one live transaction: the coordinator issues no longer one, and no second live one, and fails its run rather than answer with it.", "Part 4, section 4.5.4",
            Tests: [nameof(AmqpTransactionCoordinatorTests.DeclareAsync_OnCoordinator_ShouldReturnDeclaredIdentifiers)],
            Refusals:
            [
                nameof(AmqpTransactionCoordinatorTests.RunAsync_OnIdentifierLongerThan32Bytes_ShouldFailAndRollBack),
                nameof(AmqpTransactionCoordinatorTests.RunAsync_OnDuplicateIdentifier_ShouldFailAndRollBack)
            ]),
        new("T13", "The coordinator announces its capabilities and the controller checks they cover what it asked for; a coordinator that announces none is taken to support them.", "Part 4, sections 4.5.1 and 4.5.7",
            Tests: [nameof(AmqpTransactionCoordinatorTests.AttachAsync_OnCoordinatorWithoutCapabilities_ShouldAttach)],
            Refusals: [nameof(AmqpTransactionCoordinatorTests.AttachAsync_OnMissingCapability_ShouldThrowTransactionFailed)],
            Leniency: true),
        new("T14", "Transactional posting carries the transaction on the transfer and the resource's provisional outcome on its disposition.", "Part 4, section 4.4.1",
            Tests: [nameof(AmqpTransactionCoordinatorTests.TransactionalPosting_OnSplitDelivery_ShouldCarryProvisionalOutcome)],
            Refusals: [])
    ];

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a control link that carries a settled declare with amqp:illegal-state")]
    public async Task OnTransfer_OnControlLinkSettled_ShouldDetachWithIllegalState()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpCoordinator(), null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, settled: true), timeout.Token, payload: _declarePayload);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Closed.ShouldBeTrue();
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.IllegalState);
        receiver.LinkDetached.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a control link that carries transactional work with amqp:not-allowed")]
    public async Task OnTransfer_OnControlLinkTransactional_ShouldDetachWithNotAllowed()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        await AttachPeerSenderAsync(peer, session, new AmqpCoordinator(), null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, state: new AmqpTransactionalState(_transactionA)), timeout.Token, payload: _declarePayload);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotAllowed);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDisposition: Should detach a control link whose sender moves a declare into a transaction with amqp:not-allowed")]
    public async Task OnDisposition_OnControlLinkTransactional_ShouldDetachWithNotAllowed()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpCoordinator(), null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0), timeout.Token, payload: _declarePayload);
        await receiver.ReceiveAsync(timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative { Role = AmqpRole.Sender, First = 0, State = new AmqpTransactionalState(_transactionA) }, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotAllowed);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SettleAsync: Should refuse to answer a declare with a transactional state")]
    public async Task SettleAsync_OnControlLinkWithTransactionalState_ShouldRefuse()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpCoordinator(), null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0), timeout.Token, payload: _declarePayload);
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            async () => await delivery.SettleAsync(new AmqpTransactionalState(_transactionA, AmqpAccepted.Instance), timeout.Token));

        // Assert
        exception.ParamName.ShouldBe("state");
        delivery.IsSettled.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SetAcquisitionTransaction: Should refuse to acquire transactionally on a control link")]
    public async Task SetAcquisitionTransaction_OnControlLink_ShouldRefuse()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpCoordinator(), null, timeout.Token);

        // Act
        Action act = () => receiver.SetAcquisitionTransaction(_transactionA);

        // Assert
        act.ShouldThrow<InvalidOperationException>();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should refuse a settled or transactional delivery on a control link")]
    public async Task SendAsync_OnControlLink_ShouldRefuseSettledAndTransactionalDeliveries()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpSender sender = await AttachControlSenderAsync(peer, session, timeout.Token);

        // Act
        ArgumentException settled = await Should.ThrowAsync<ArgumentException>(
            async () => await sender.SendAsync(_declarePayload, new AmqpDeliveryOptions { Settled = true }, timeout.Token));
        ArgumentException transactional = await Should.ThrowAsync<ArgumentException>(
            async () => await sender.SendAsync(_declarePayload, new AmqpDeliveryOptions { State = new AmqpTransactionalState(_transactionA) }, timeout.Token));

        // Assert
        settled.ParamName.ShouldBe("options");
        transactional.ParamName.ShouldBe("options");
        sender.Credit.ShouldBe(10u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDisposition: Should detach a control link whose coordinator answers with a transactional state with amqp:not-allowed")]
    public async Task OnDisposition_OnTransactionalAnswerFromCoordinator_ShouldDetachWithNotAllowed()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpSender sender = await AttachControlSenderAsync(peer, session, timeout.Token);
        AmqpTransactionController controller = AmqpTransactionController.Create(sender);
        Task<ReadOnlyMemory<byte>> declare = controller.DeclareAsync(timeout.Token).AsTask();
        AmqpTransferPerformative transfer = await ReadUntilAsync<AmqpTransferPerformative>(peer, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative
        {
            Role = AmqpRole.Receiver,
            First = transfer.DeliveryId.ShouldNotBeNull(),
            State = new AmqpTransactionalState(_transactionA, AmqpAccepted.Instance)
        }, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(declare);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotAllowed);
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AttachSenderAsync: Should refuse a link to a coordinator that sends settled")]
    public async Task AttachSenderAsync_ToCoordinatorSendingSettled_ShouldRefuse()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        AmqpSenderOptions options = new()
        {
            Target = new AmqpCoordinator(),
            SenderSettleMode = AmqpSenderSettleMode.Settled
        };

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(async () => await session.AttachSenderAsync(options, timeout.Token));

        // Assert
        exception.ParamName.ShouldBe("options");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AttachSenderAsync: Should refuse to resume a link to a coordinator")]
    public async Task AttachSenderAsync_ToCoordinatorResuming_ShouldRefuse()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        AmqpSenderOptions options = new()
        {
            Target = new AmqpCoordinator(),
            Unsettled = new Dictionary<AmqpDeliveryTag, AmqpDeliveryState?> { [AmqpDeliveryTag.Create(1UL)] = null }
        };

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(async () => await session.AttachSenderAsync(options, timeout.Token));

        // Assert
        exception.ParamName.ShouldBe("options");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should name the transaction on every frame of a split delivery")]
    public async Task SendAsync_OnSplitTransactionalDelivery_ShouldNameTransactionOnEveryFrame()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(null, AmqpTestPeer.CreateOpen(maxFrameSize: 512), timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateLinkFlow(engineBegin, credit: 1), timeout.Token);

        // Act
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[1500], new AmqpDeliveryOptions { Settled = true, State = new AmqpTransactionalState(_transactionA) }, timeout.Token).AsTask();
        List<AmqpTransferPerformative> frames = await ReadDeliveryAsync(peer, timeout.Token);
        await send;

        // Assert
        frames.Count.ShouldBeGreaterThan(2);

        foreach (AmqpTransferPerformative frame in frames)
        {
            frame.State.ShouldBeOfType<AmqpTransactionalState>().TransactionId.ToArray().ShouldBe(_transactionA);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SendAsync: Should name the transaction on the frame that aborts a canceled delivery")]
    public async Task SendAsync_OnCanceledTransactionalDelivery_ShouldNameTransactionOnAbort()
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
        Task<AmqpDeliveryState?> send = sender.SendAsync(new byte[1500], new AmqpDeliveryOptions { State = new AmqpTransactionalState(_transactionA) }, cancel.Token).AsTask();
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
        AmqpTransferPerformative abort = await ReadUntilAsync<AmqpTransferPerformative>(peer, timeout.Token);
        await Should.ThrowAsync<OperationCanceledException>(send);

        // Assert
        first.More.ShouldBeTrue();
        abort.Aborted.ShouldBeTrue();
        abort.State.ShouldBeOfType<AmqpTransactionalState>().TransactionId.ToArray().ShouldBe(_transactionA);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose later frame names another transaction with amqp:invalid-field")]
    public async Task OnTransfer_OnContinuationInAnotherTransaction_ShouldDetachWithInvalidField()
    {
        // Arrange, Act and Assert
        await AssertContinuationRefusedAsync(new AmqpTransactionalState(_transactionA), new AmqpTransactionalState(_transactionB));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose later frame moves a delivery out of its transaction with amqp:invalid-field")]
    public async Task OnTransfer_OnContinuationOutOfTransaction_ShouldDetachWithInvalidField()
    {
        // Arrange, Act and Assert
        await AssertContinuationRefusedAsync(new AmqpTransactionalState(_transactionA), new AmqpReceived(0, 0));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should detach a link whose later frame moves a delivery into a transaction with amqp:invalid-field")]
    public async Task OnTransfer_OnContinuationIntoTransaction_ShouldDetachWithInvalidField()
    {
        // Arrange, Act and Assert
        await AssertContinuationRefusedAsync(null, new AmqpTransactionalState(_transactionA));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnTransfer: Should keep a delivery in its transaction when a later frame carries no state")]
    public async Task OnTransfer_OnContinuationWithoutState_ShouldKeepTransaction()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);
        AmqpDeliveryState? before = receiver.PartialDeliveryState;

        // Act
        AmqpTransferPerformative first = CreateTransfer(deliveryId: 0, state: new AmqpTransactionalState(_transactionA));
        first.More = true;
        await peer.WriteFrameAsync(0, first, timeout.Token, payload: new byte[] { 0x00, 0x53 });
        AmqpDeliveryState? partial = await WaitForPartialStateAsync(receiver, timeout.Token);
        await peer.WriteFrameAsync(0, new AmqpTransferPerformative { Handle = 0 }, timeout.Token, payload: new byte[] { 0x77, 0x40 });
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Assert
        before.ShouldBeNull();
        partial.ShouldBeOfType<AmqpTransactionalState>().TransactionId.ToArray().ShouldBe(_transactionA);
        receiver.PartialDeliveryState.ShouldBeNull();
        delivery.RemoteState.ShouldBeOfType<AmqpTransactionalState>().TransactionId.ToArray().ShouldBe(_transactionA);
        delivery.Payload.ToArray().ShouldBe(new byte[] { 0x00, 0x53, 0x77, 0x40 });
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnDisposition: Should detach a link whose sender moves a posted delivery to another transaction with amqp:invalid-field")]
    public async Task OnDisposition_OnSenderMovingDelivery_ShouldDetachWithInvalidField()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);
        await peer.WriteFrameAsync(0, CreateTransfer(deliveryId: 0, state: new AmqpTransactionalState(_transactionA)), timeout.Token, payload: _declarePayload);
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, new AmqpDispositionPerformative { Role = AmqpRole.Sender, First = 0, State = new AmqpTransactionalState(_transactionB) }, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        delivery.RemoteState.ShouldBeOfType<AmqpTransactionalState>().TransactionId.ToArray().ShouldBe(_transactionA);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnFlow: Should detach a sender without transactional acquisition when a flow names a transaction")]
    public async Task OnFlow_WithTransactionIdWithoutSupport_ShouldDetachWithNotImplemented()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, null, timeout.Token);

        // Act
        await peer.WriteFrameAsync(0, CreateLinkFlow(engineBegin, credit: 1, transactionId: _transactionA), timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotImplemented);
        sender.AcquisitionTransactionId.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnFlow: Should detach a sender without transactional acquisition once answered when a flow named a transaction before")]
    public async Task OnFlow_WithTransactionIdBeforeAnswer_ShouldDetachWhenAnswered()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        await peer.WriteFrameAsync(0, CreateReceiverAttach(new AmqpTarget { Address = "peer" }), timeout.Token);
        IAmqpLinkRequest request = await session.AcceptLinkAsync(timeout.Token);
        await peer.WriteFrameAsync(0, CreateLinkFlow(engineBegin, credit: 1, transactionId: _transactionA), timeout.Token);

        // A session flow the engine echoes proves the link's flow was applied before the answer.
        await peer.WriteFrameAsync(0, new AmqpFlowPerformative { NextIncomingId = engineBegin.NextOutgoingId, IncomingWindow = 100, NextOutgoingId = 1, OutgoingWindow = 100, Echo = true }, timeout.Token);
        await ReadUntilAsync<AmqpFlowPerformative>(peer, timeout.Token);

        // Act
        IAmqpSender sender = await request.AcceptSenderAsync(null, timeout.Token);
        AmqpAttachPerformative answer = await ReadUntilAsync<AmqpAttachPerformative>(peer, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        answer.Target.ShouldNotBeNull();
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotImplemented);
        sender.LinkDetached.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnFlow: Should keep the transaction a flow names until the application clears it")]
    public async Task OnFlow_WithTransactionId_ShouldKeepItUntilCleared()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        IAmqpSender sender = await AttachPeerReceiverAsync(peer, session, new AmqpSenderOptions { TransactionalAcquisition = true }, timeout.Token);

        // Act
        await WriteFlowAndWaitAsync(peer, CreateLinkFlow(engineBegin, credit: 5, transactionId: _transactionA), timeout.Token);
        ReadOnlyMemory<byte>? named = sender.AcquisitionTransactionId;
        await WriteFlowAndWaitAsync(peer, CreateLinkFlow(engineBegin, credit: 6), timeout.Token);
        ReadOnlyMemory<byte>? kept = sender.AcquisitionTransactionId;
        bool clearedOther = sender.ClearAcquisitionTransaction(_transactionB);
        bool cleared = sender.ClearAcquisitionTransaction(_transactionA);

        // Assert
        named.ShouldNotBeNull().ToArray().ShouldBe(_transactionA);
        kept.ShouldNotBeNull().ToArray().ShouldBe(_transactionA);
        clearedOther.ShouldBeFalse();
        cleared.ShouldBeTrue();
        sender.AcquisitionTransactionId.ShouldBeNull();
        sender.Credit.ShouldBe(6u);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - OnFlow: Should detach a sender whose flow names a transaction that is not binary with amqp:invalid-field")]
    public async Task OnFlow_WithTransactionIdNotBinary_ShouldDetachWithInvalidField()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        AmqpBeginPerformative engineBegin = await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);
        await AttachPeerReceiverAsync(peer, session, new AmqpSenderOptions { TransactionalAcquisition = true }, timeout.Token);
        AmqpFlowPerformative flow = CreateLinkFlow(engineBegin, credit: 1);
        flow.Properties = new Dictionary<AmqpSymbol, object?> { [new AmqpSymbol("txn-id")] = "not-binary" };

        // Act
        await peer.WriteFrameAsync(0, flow, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SetAcquisitionTransaction: Should name the transaction in every flow until told to name none")]
    public async Task SetAcquisitionTransaction_OnReceiver_ShouldNameTransactionInEveryFlow()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, new AmqpReceiverOptions { Prefetch = 0 }, timeout.Token);

        // Act
        receiver.SetAcquisitionTransaction(_transactionA);
        AmqpFlowPerformative named = await ReadUntilAsync<AmqpFlowPerformative>(peer, timeout.Token);
        receiver.AddCredit(3);
        AmqpFlowPerformative repeated = await ReadUntilAsync<AmqpFlowPerformative>(peer, timeout.Token);
        receiver.SetAcquisitionTransaction(null);
        AmqpFlowPerformative cleared = await ReadUntilAsync<AmqpFlowPerformative>(peer, timeout.Token);

        // Assert
        named.Handle.ShouldBe(0u);
        named.Properties.ShouldNotBeNull()[new AmqpSymbol("txn-id")].ShouldBeOfType<byte[]>().ShouldBe(_transactionA);
        repeated.LinkCredit.ShouldBe(3u);
        repeated.Properties.ShouldNotBeNull()[new AmqpSymbol("txn-id")].ShouldBeOfType<byte[]>().ShouldBe(_transactionA);
        cleared.Properties.ShouldBeNull();
    }

    private static readonly byte[] _declarePayload = [0x00, 0x53, 0x77, 0x00, 0x53, 0x31, 0x45];

    private static async Task AssertContinuationRefusedAsync(AmqpDeliveryState? firstState, AmqpDeliveryState laterState)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        IAmqpSession session = await BeginAsync(host, timeout.Token);
        IAmqpReceiver receiver = await AttachPeerSenderAsync(peer, session, new AmqpTarget { Address = "queue" }, null, timeout.Token);
        AmqpTransferPerformative first = CreateTransfer(deliveryId: 0, state: firstState);
        first.More = true;
        await peer.WriteFrameAsync(0, first, timeout.Token, payload: new byte[] { 0x00, 0x53 });

        // Act
        await peer.WriteFrameAsync(0, new AmqpTransferPerformative { Handle = 0, State = laterState }, timeout.Token, payload: new byte[] { 0x77, 0x40 });
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);

        // Assert
        detach.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        receiver.TryReceive(out _).ShouldBeFalse();
    }

    /// <summary>
    /// The engine attaches a control link to the peer, which answers as a coordinator and grants credit.
    /// </summary>
    private static async Task<IAmqpSender> AttachControlSenderAsync(AmqpTestPeer peer, IAmqpSession session, CancellationToken cancellationToken)
    {
        Task<IAmqpSender> attach = session.AttachSenderAsync(
            new AmqpSenderOptions
            {
                Source = new AmqpSource { Address = "controller" },
                Target = new AmqpCoordinator()
            },
            cancellationToken).AsTask();
        AmqpAttachPerformative request = await ReadUntilAsync<AmqpAttachPerformative>(peer, cancellationToken);

        await peer.WriteFrameAsync(0, new AmqpAttachPerformative
        {
            Name = request.Name,
            Handle = 0,
            Role = AmqpRole.Receiver,
            Source = request.Source,
            Target = new AmqpCoordinator { Capabilities = [AmqpTransactionCapability.LocalTransactions] }
        }, cancellationToken);

        IAmqpSender sender = await attach;

        await peer.WriteFrameAsync(0, new AmqpFlowPerformative
        {
            IncomingWindow = 100,
            NextOutgoingId = 1,
            OutgoingWindow = 100,
            Handle = 0,
            DeliveryCount = 0,
            LinkCredit = 10,
            Echo = true
        }, cancellationToken);

        // The engine's echo proves the credit arrived.
        await ReadUntilAsync<AmqpFlowPerformative>(peer, cancellationToken);
        return sender;
    }
}
