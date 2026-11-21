using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Runs an <see cref="AmqpTransactionController"/> against an <see cref="AmqpTransactionCoordinator"/> over a
/// real connection (AMQP 1.0 Part 4): declares, discharges and their refusals, the rollback when the control
/// link ends, and transactional posting and acquisition on the controller's session.
/// </summary>
public class AmqpTransactionCoordinatorTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DeclareAsync: Should return the identifier the coordinator declared for each transaction")]
    public async Task DeclareAsync_OnCoordinator_ShouldReturnDeclaredIdentifiers()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new();
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);

        // Act
        ReadOnlyMemory<byte> first = await controller.DeclareAsync(timeout.Token);
        ReadOnlyMemory<byte> second = await controller.DeclareAsync(timeout.Token);

        // Assert
        Hex(first).ShouldBe("01");
        Hex(second).ShouldBe("02");
        handler.Declares.Count.ShouldBe(2);
        handler.Declares.ShouldAllBe(declare => declare.GlobalId == null);
        coordinator.GetActiveTransactions().Select(Hex).ToArray().ShouldBe(new[] { "01", "02" });
        controller.Capabilities.ShouldNotBeNull().ShouldContain(AmqpTransactionCapability.LocalTransactions);
        run.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DischargeAsync: Should commit the transaction and keep it out of the rollback when the link ends")]
    public async Task DischargeAsync_OnCommit_ShouldCommitAndEndTransaction()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new();
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);
        ReadOnlyMemory<byte> transaction = await controller.DeclareAsync(timeout.Token);

        // Act
        await controller.DischargeAsync(transaction, fail: false, timeout.Token);
        IReadOnlyList<ReadOnlyMemory<byte>> active = coordinator.GetActiveTransactions();
        await controller.DisposeAsync();
        await run.WaitAsync(timeout.Token);

        // Assert
        (byte[] TransactionId, bool Fail) discharge = handler.Discharges.ShouldHaveSingleItem();
        Hex(discharge.TransactionId).ShouldBe(Hex(transaction));
        discharge.Fail.ShouldBeFalse();
        active.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DischargeAsync: Should roll the transaction back and end it")]
    public async Task DischargeAsync_OnRollback_ShouldRollBackAndEndTransaction()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new();
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);
        ReadOnlyMemory<byte> transaction = await controller.DeclareAsync(timeout.Token);

        // Act
        await controller.DischargeAsync(transaction, fail: true, timeout.Token);
        IReadOnlyList<ReadOnlyMemory<byte>> active = coordinator.GetActiveTransactions();
        await controller.DisposeAsync();
        await run.WaitAsync(timeout.Token);

        // Assert
        await handler.RolledBack.WaitAsync(timeout.Token);
        (byte[] TransactionId, bool Fail) discharge = handler.Discharges.ShouldHaveSingleItem();
        Hex(discharge.TransactionId).ShouldBe(Hex(transaction));
        discharge.Fail.ShouldBeTrue();
        active.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DeclareAsync: Should throw with the resource's error when it refuses a declare, and keep the link")]
    public async Task DeclareAsync_OnHandlerRefusal_ShouldThrowWithTransactionError()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new()
        {
            DeclareFailure = new AmqpException(AmqpErrorCode.TransactionFailed, "No more transactions.")
            {
                Error = new AmqpError(AmqpErrorCondition.ResourceLimitExceeded, "No more transactions.")
            }
        };
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(async () => await controller.DeclareAsync(timeout.Token));
        handler.DeclareFailure = null;
        ReadOnlyMemory<byte> next = await controller.DeclareAsync(timeout.Token);

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.TransactionFailed);
        AmqpError error = exception.Error.ShouldNotBeNull();
        error.Condition.ShouldBe(AmqpErrorCondition.ResourceLimitExceeded);
        error.Description.ShouldBe("No more transactions.");
        Hex(next).ShouldBe("01");
        Hex(coordinator.GetActiveTransactions().ShouldHaveSingleItem()).ShouldBe("01");
        run.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DischargeAsync: Should throw with the resource's error when it refuses a commit, and end the transaction")]
    public async Task DischargeAsync_OnCommitRefusal_ShouldThrowAndEndTransaction()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new()
        {
            CommitFailure = new AmqpException(AmqpErrorCode.TransactionFailed, "Rolled back.")
            {
                Error = new AmqpError(AmqpErrorCondition.TransactionRollback, "The resource rolled the transaction back.")
            }
        };
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);
        ReadOnlyMemory<byte> transaction = await controller.DeclareAsync(timeout.Token);

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(async () => await controller.DischargeAsync(transaction, cancellationToken: timeout.Token));
        AmqpException again = await Should.ThrowAsync<AmqpException>(async () => await controller.DischargeAsync(transaction, fail: true, timeout.Token));
        await controller.DisposeAsync();
        await run.WaitAsync(timeout.Token);

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.TransactionFailed);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.TransactionRollback);
        again.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.TransactionUnknownId);
        handler.Discharges.ShouldHaveSingleItem().Fail.ShouldBeFalse();
        coordinator.GetActiveTransactions().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DischargeAsync: Should refuse with amqp:transaction:unknown-id a transaction the link never declared, another link's included")]
    public async Task DischargeAsync_OnUnknownTransaction_ShouldRefuseWithUnknownId()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new();
        (AmqpTransactionController first, AmqpTransactionCoordinator firstCoordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        (AmqpTransactionController second, AmqpTransactionCoordinator secondCoordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task firstRun = firstCoordinator.RunAsync(timeout.Token);
        Task secondRun = secondCoordinator.RunAsync(timeout.Token);
        ReadOnlyMemory<byte> others = await second.DeclareAsync(timeout.Token);

        // Act
        AmqpException never = await Should.ThrowAsync<AmqpException>(async () => await first.DischargeAsync(new byte[] { 0x7F }, cancellationToken: timeout.Token));
        AmqpException foreign = await Should.ThrowAsync<AmqpException>(async () => await first.DischargeAsync(others, cancellationToken: timeout.Token));

        // Assert
        never.ErrorCode.ShouldBe(AmqpErrorCode.TransactionFailed);
        never.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.TransactionUnknownId);
        foreign.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.TransactionUnknownId);
        handler.Discharges.ShouldBeEmpty();
        Hex(secondCoordinator.GetActiveTransactions().ShouldHaveSingleItem()).ShouldBe(Hex(others));
        firstRun.IsCompleted.ShouldBeFalse();
        secondRun.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - DisposeAsync: Should roll back every transaction the control link left live, in the order declared")]
    public async Task DisposeAsync_OnControllerWithLiveTransactions_ShouldRollThemBack()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new() { ExpectedRollbacks = 2 };
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);
        await controller.DeclareAsync(timeout.Token);
        await controller.DeclareAsync(timeout.Token);

        // Act
        await controller.DisposeAsync();
        await run.WaitAsync(timeout.Token);

        // Assert
        await handler.RolledBack.WaitAsync(timeout.Token);
        handler.Discharges.Select(entry => (Hex(entry.TransactionId), entry.Fail)).ToArray().ShouldBe(new[] { ("01", true), ("02", true) });
        coordinator.GetActiveTransactions().ShouldBeEmpty();
        coordinator.Link.LinkDetached.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - RunAsync: Should roll back every transaction and throw the first failure when a rollback fails")]
    public async Task RunAsync_OnFailedRollback_ShouldRollBackTheOthersAndThrow()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        InvalidOperationException rollbackFailure = new("The resource could not roll back.");
        TestTransactionHandler handler = new() { ExpectedRollbacks = 2, RollbackFailure = rollbackFailure };
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);
        await controller.DeclareAsync(timeout.Token);
        await controller.DeclareAsync(timeout.Token);

        // Act
        await controller.DisposeAsync();
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(async () => await run.WaitAsync(timeout.Token));

        // Assert
        exception.ShouldBeSameAs(rollbackFailure);
        handler.Discharges.Select(entry => (Hex(entry.TransactionId), entry.Fail)).ToArray().ShouldBe(new[] { ("01", true), ("02", true) });
        coordinator.GetActiveTransactions().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - RunAsync: Should close the control link, roll back its transactions and throw when canceled")]
    public async Task RunAsync_OnCancellation_ShouldCloseLinkAndRollBack()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new();
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(stop.Token);
        ReadOnlyMemory<byte> transaction = await controller.DeclareAsync(timeout.Token);

        // Act
        stop.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(run);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(async () => await controller.DeclareAsync(timeout.Token));

        // Assert
        await handler.RolledBack.WaitAsync(timeout.Token);
        (byte[] TransactionId, bool Fail) rollback = handler.Discharges.ShouldHaveSingleItem();
        Hex(rollback.TransactionId).ShouldBe(Hex(transaction));
        rollback.Fail.ShouldBeTrue();
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        controller.Link.LinkDetached.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - RunAsync: Should close the control link with amqp:internal-error, roll back and throw when the resource faults")]
    public async Task RunAsync_OnHandlerFault_ShouldCloseWithInternalErrorAndRollBack()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new()
        {
            CommitFailure = new InvalidOperationException("The store is down."),
            ExpectedRollbacks = 2
        };
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);
        ReadOnlyMemory<byte> first = await controller.DeclareAsync(timeout.Token);
        await controller.DeclareAsync(timeout.Token);

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(async () => await controller.DischargeAsync(first, cancellationToken: timeout.Token));
        InvalidOperationException fault = await Should.ThrowAsync<InvalidOperationException>(run);

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InternalError);
        fault.Message.ShouldBe("The store is down.");
        await handler.RolledBack.WaitAsync(timeout.Token);
        handler.Discharges.Select(entry => (Hex(entry.TransactionId), entry.Fail)).ToArray().ShouldBe(new[] { ("01", false), ("01", true), ("02", true) });
        coordinator.GetActiveTransactions().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - RunAsync: Should declare a 32-byte identifier, and fail and roll back on a longer one")]
    public async Task RunAsync_OnIdentifierLongerThan32Bytes_ShouldFailAndRollBack()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new()
        {
            CreateTransactionId = static next => Enumerable.Repeat((byte)next, next == 1 ? 32 : 33).ToArray(),
            ExpectedRollbacks = 2
        };
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);

        // Act
        ReadOnlyMemory<byte> longest = await controller.DeclareAsync(timeout.Token);
        AmqpException exception = await Should.ThrowAsync<AmqpException>(async () => await controller.DeclareAsync(timeout.Token));
        await Should.ThrowAsync<InvalidOperationException>(run);

        // Assert
        longest.Length.ShouldBe(32);
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InternalError);
        await handler.RolledBack.WaitAsync(timeout.Token);
        handler.Discharges.Select(entry => (entry.TransactionId.Length, entry.Fail)).ToArray().ShouldBe(new[] { (32, true), (33, true) });
        coordinator.GetActiveTransactions().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - RunAsync: Should fail and roll back when the resource declares an identifier already live on the link")]
    public async Task RunAsync_OnDuplicateIdentifier_ShouldFailAndRollBack()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new() { CreateTransactionId = static _ => [0x01] };
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);
        await controller.DeclareAsync(timeout.Token);

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(async () => await controller.DeclareAsync(timeout.Token));
        await Should.ThrowAsync<InvalidOperationException>(run);

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.InternalError);
        await handler.RolledBack.WaitAsync(timeout.Token);
        (byte[] TransactionId, bool Fail) rollback = handler.Discharges.ShouldHaveSingleItem();
        Hex(rollback.TransactionId).ShouldBe("01");
        rollback.Fail.ShouldBeTrue();
        coordinator.GetActiveTransactions().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - RunAsync: Should reject with amqp:not-implemented a message that is not a declare or a discharge")]
    public async Task RunAsync_OnMessageThatIsNotControl_ShouldRejectWithNotImplemented()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new();
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);

        // Act
        AmqpDeliveryState? value = await controller.Link.SendAsync(new AmqpMessage { Body = new AmqpValueBody("commit everything") }, null, timeout.Token);
        AmqpDeliveryState? data = await controller.Link.SendAsync(new AmqpMessage { Body = new AmqpDataBody(new byte[] { 0x01 }) }, null, timeout.Token);
        ReadOnlyMemory<byte> transaction = await controller.DeclareAsync(timeout.Token);

        // Assert
        value.ShouldBeOfType<AmqpRejected>().Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotImplemented);
        data.ShouldBeOfType<AmqpRejected>().Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotImplemented);
        Hex(transaction).ShouldBe("01");
        handler.Declares.Count.ShouldBe(1);
        run.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - RunAsync: Should reject with amqp:decode-error a control message that does not decode")]
    public async Task RunAsync_OnUndecodablePayload_ShouldRejectWithDecodeError()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new();
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);

        // Act: an amqp-value section whose str8 value stops before its length.
        AmqpDeliveryState? outcome = await controller.Link.SendAsync(new byte[] { 0x00, 0x53, 0x77, 0xA1 }, null, timeout.Token);
        ReadOnlyMemory<byte> transaction = await controller.DeclareAsync(timeout.Token);

        // Assert
        outcome.ShouldBeOfType<AmqpRejected>().Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.DecodeError);
        Hex(transaction).ShouldBe("01");
        run.IsCompleted.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - RunAsync: Should close the control link with the error when the controller's source does not support rejected")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_OnSourceWithoutRejected_ShouldDetachWithError(bool listsOutcomes)
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new();
        Task<AmqpTransactionCoordinator> accept = AcceptCoordinatorAsync(server, handler, null, timeout.Token);

        // A source that lists no outcomes supports only accepted (AMQP 1.0 Part 3, section 3.5.3).
        IAmqpSender link = await client.AttachSenderAsync(
            new AmqpSenderOptions
            {
                Source = listsOutcomes ? new AmqpSource { Outcomes = [AmqpAccepted.Descriptor, AmqpDeclared.Descriptor] } : new AmqpSource(),
                Target = new AmqpCoordinator()
            },
            timeout.Token);
        AmqpTransactionCoordinator coordinator = await accept;
        AmqpTransactionController controller = AmqpTransactionController.Create(link);
        Task run = coordinator.RunAsync(timeout.Token);

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(async () => await controller.DischargeAsync(new byte[] { 0x7F }, cancellationToken: timeout.Token));
        await run.WaitAsync(timeout.Token);

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.TransactionUnknownId);
        coordinator.Link.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        handler.Discharges.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AttachAsync: Should attach the control link unsettled, from a source that supports rejected")]
    public async Task AttachAsync_OnSession_ShouldAttachUnsettledFromSourceSupportingRejected()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        Task<IAmqpLinkRequest> accept = server.AcceptLinkAsync(timeout.Token).AsTask();

        // Act
        Task<AmqpTransactionController> attach = AmqpTransactionController.AttachAsync(client, cancellationToken: timeout.Token).AsTask();
        IAmqpLinkRequest request = await accept;
        await using AmqpTransactionCoordinator coordinator = await AmqpTransactionCoordinator.AcceptAsync(request, new TestTransactionHandler(), cancellationToken: timeout.Token);
        await using AmqpTransactionController controller = await attach;

        // Assert
        request.Target.ShouldBeOfType<AmqpCoordinator>();
        request.SenderSettleMode.ShouldBe(AmqpSenderSettleMode.Unsettled);
        request.Source.ShouldNotBeNull().Outcomes.ShouldNotBeNull().ShouldContain(AmqpRejected.Descriptor);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AttachAsync: Should close the link and throw when the coordinator lacks a capability the controller asked for")]
    public async Task AttachAsync_OnMissingCapability_ShouldThrowTransactionFailed()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        Task<AmqpTransactionCoordinator> accept = AcceptCoordinatorAsync(server, new TestTransactionHandler(), [AmqpTransactionCapability.LocalTransactions], timeout.Token);

        // Act
        AmqpException exception = await Should.ThrowAsync<AmqpException>(async () => await AmqpTransactionController.AttachAsync(
            client,
            [AmqpTransactionCapability.LocalTransactions, AmqpTransactionCapability.DistributedTransactions],
            timeout.Token));
        AmqpTransactionCoordinator coordinator = await accept;
        AmqpException detached = await Should.ThrowAsync<AmqpException>(async () => await coordinator.Link.ReceiveAsync(timeout.Token));

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.TransactionFailed);
        exception.Message.ShouldContain("amqp:distributed-transactions", Case.Sensitive);
        detached.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotImplemented);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AttachAsync: Should attach to a coordinator that announces no capabilities")]
    public async Task AttachAsync_OnCoordinatorWithoutCapabilities_ShouldAttach()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new();
        Task<AmqpTransactionCoordinator> accept = AcceptSilentCoordinatorAsync(server, handler, timeout.Token);

        // Act
        AmqpTransactionController controller = await AmqpTransactionController.AttachAsync(client, cancellationToken: timeout.Token);
        AmqpTransactionCoordinator coordinator = await accept;
        Task run = coordinator.RunAsync(timeout.Token);
        ReadOnlyMemory<byte> transaction = await controller.DeclareAsync(timeout.Token);

        // Assert
        controller.Capabilities.ShouldBeNull();
        Hex(transaction).ShouldBe("01");
        run.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - TransactionalPosting: Should carry the transaction on a split delivery and the resource's provisional outcome back")]
    public async Task TransactionalPosting_OnSplitDelivery_ShouldCarryProvisionalOutcome()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(
            new AmqpConnectionListenerOptions { MaxFrameSize = 512 },
            new AmqpConnectionFactoryOptions { MaxFrameSize = 512 },
            timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new();
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(client, server, cancellationToken: timeout.Token);
        ReadOnlyMemory<byte> transaction = await controller.DeclareAsync(timeout.Token);
        byte[] payload = [.. Enumerable.Range(0, 2000).Select(static index => (byte)index)];

        // Act
        Task<AmqpDeliveryState?> post = sender.SendAsync(payload, new AmqpDeliveryOptions { State = new AmqpTransactionalState(transaction) }, timeout.Token).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await delivery.SettleAsync(new AmqpTransactionalState(transaction, AmqpAccepted.Instance), timeout.Token);
        AmqpDeliveryState? provisional = await post;
        await controller.DischargeAsync(transaction, cancellationToken: timeout.Token);

        // Assert
        delivery.Payload.ToArray().ShouldBe(payload);
        Hex(delivery.RemoteState.ShouldBeOfType<AmqpTransactionalState>().TransactionId).ShouldBe(Hex(transaction));
        AmqpTransactionalState outcome = provisional.ShouldBeOfType<AmqpTransactionalState>();
        Hex(outcome.TransactionId).ShouldBe(Hex(transaction));
        outcome.Outcome.ShouldBeOfType<AmqpAccepted>();
        handler.Discharges.ShouldHaveSingleItem().Fail.ShouldBeFalse();
        receiver.LinkDetached.IsCancellationRequested.ShouldBeFalse();
        run.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - TransactionalAcquisition: Should carry the transaction from the receiver's flow to the delivery and its outcome")]
    public async Task TransactionalAcquisition_OnSupportingSender_ShouldCarryTransactionToCommit()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        TestTransactionHandler handler = new();
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, handler, timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);
        (IAmqpReceiver receiver, IAmqpSender sender) = await AttachAcquiringLinkAsync(client, server, new AmqpSenderOptions { TransactionalAcquisition = true }, timeout.Token);
        ReadOnlyMemory<byte> transaction = await controller.DeclareAsync(timeout.Token);

        // Act
        receiver.SetAcquisitionTransaction(transaction);
        receiver.AddCredit(1);
        await sender.WaitForCreditAsync(timeout.Token);
        ReadOnlyMemory<byte> acquiredUnder = sender.AcquisitionTransactionId.ShouldNotBeNull();
        Task<AmqpDeliveryState?> offer = sender.SendAsync(
            new byte[] { 0x00, 0x53, 0x77, 0x40 },
            new AmqpDeliveryOptions { State = new AmqpTransactionalState(acquiredUnder) },
            timeout.Token).AsTask();
        IAmqpDelivery delivery = await receiver.ReceiveAsync(timeout.Token);
        await delivery.SettleAsync(new AmqpTransactionalState(transaction, AmqpAccepted.Instance), timeout.Token);
        AmqpDeliveryState? acquired = await offer;
        await controller.DischargeAsync(transaction, cancellationToken: timeout.Token);
        bool cleared = sender.ClearAcquisitionTransaction(transaction);

        // Assert
        Hex(acquiredUnder).ShouldBe(Hex(transaction));
        Hex(delivery.RemoteState.ShouldBeOfType<AmqpTransactionalState>().TransactionId).ShouldBe(Hex(transaction));
        AmqpTransactionalState outcome = acquired.ShouldBeOfType<AmqpTransactionalState>();
        Hex(outcome.TransactionId).ShouldBe(Hex(transaction));
        outcome.Outcome.ShouldBeOfType<AmqpAccepted>();
        handler.Discharges.ShouldHaveSingleItem().Fail.ShouldBeFalse();
        cleared.ShouldBeTrue();
        sender.AcquisitionTransactionId.ShouldBeNull();
        run.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - SetAcquisitionTransaction: Should end the link with amqp:not-implemented when the sender does not acquire transactionally")]
    public async Task SetAcquisitionTransaction_OnSenderWithoutSupport_ShouldDetachWithNotImplemented()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (IAmqpReceiver receiver, IAmqpSender sender) = await AttachAcquiringLinkAsync(client, server, null, timeout.Token);

        // Act
        receiver.SetAcquisitionTransaction(new byte[] { 0x01 });
        AmqpException exception = await Should.ThrowAsync<AmqpException>(async () => await receiver.ReceiveAsync(timeout.Token));

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.LinkDetached);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.NotImplemented);
        sender.LinkDetached.IsCancellationRequested.ShouldBeTrue();
        sender.AcquisitionTransactionId.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - RunAsync: Should refuse a second run and keep the first one serving")]
    public async Task RunAsync_OnSecondCall_ShouldThrow()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (AmqpTransactionController controller, AmqpTransactionCoordinator coordinator) = await AttachControlLinkAsync(client, server, new TestTransactionHandler(), timeout.Token);
        Task run = coordinator.RunAsync(timeout.Token);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(async () => await coordinator.RunAsync(timeout.Token));
        ReadOnlyMemory<byte> transaction = await controller.DeclareAsync(timeout.Token);

        // Assert
        Hex(transaction).ShouldBe("01");
        run.IsCompleted.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - AcceptAsync: Should refuse a request that is not a link to a coordinator and leave it unanswered")]
    public async Task AcceptAsync_OnRequestThatIsNotCoordinator_ShouldThrow()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        Task<IAmqpSender> attach = client.AttachSenderAsync(
            new AmqpSenderOptions
            {
                Source = new AmqpSource { Address = "client" },
                Target = new AmqpTarget { Address = "queue" }
            },
            timeout.Token).AsTask();
        IAmqpLinkRequest request = await server.AcceptLinkAsync(timeout.Token);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            async () => await AmqpTransactionCoordinator.AcceptAsync(request, new TestTransactionHandler(), cancellationToken: timeout.Token));
        IAmqpReceiver receiver = await request.AcceptReceiverAsync(null, timeout.Token);
        IAmqpSender sender = await attach;

        // Assert
        exception.ParamName.ShouldBe("request");
        receiver.Target.ShouldBeOfType<AmqpTarget>().Address.ShouldBe("queue");
        sender.LinkDetached.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Create: Should refuse a coordinator or a controller over a link that is not a control link")]
    public async Task Create_OnLinkThatIsNotCoordinator_ShouldThrow()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpTestHost host = await AmqpTestHost.StartAsync(cancellationToken: timeout.Token);
        (IAmqpSession client, IAmqpSession server) = await host.BeginSessionAsync(cancellationToken: timeout.Token);
        (IAmqpSender sender, IAmqpReceiver receiver) = await AmqpTestHost.AttachAsync(client, server, cancellationToken: timeout.Token);

        // Act
        ArgumentException coordinator = Should.Throw<ArgumentException>(() => AmqpTransactionCoordinator.Create(receiver, new TestTransactionHandler()));
        ArgumentException controller = Should.Throw<ArgumentException>(() => AmqpTransactionController.Create(sender));

        // Assert
        coordinator.ParamName.ShouldBe("link");
        controller.ParamName.ShouldBe("link");
    }

    private static string Hex(ReadOnlyMemory<byte> value)
    {
        return Convert.ToHexString(value.Span);
    }

    /// <summary>
    /// Attaches a controller on one session and accepts its control link as a coordinator on the other.
    /// </summary>
    private static async Task<(AmqpTransactionController Controller, AmqpTransactionCoordinator Coordinator)> AttachControlLinkAsync(
        IAmqpSession controllerSession,
        IAmqpSession coordinatorSession,
        IAmqpTransactionHandler handler,
        CancellationToken cancellationToken)
    {
        Task<AmqpTransactionCoordinator> accept = AcceptCoordinatorAsync(coordinatorSession, handler, null, cancellationToken);
        AmqpTransactionController controller = await AmqpTransactionController.AttachAsync(controllerSession, cancellationToken: cancellationToken);
        AmqpTransactionCoordinator coordinator = await accept;

        return (controller, coordinator);
    }

    private static async Task<AmqpTransactionCoordinator> AcceptCoordinatorAsync(
        IAmqpSession session,
        IAmqpTransactionHandler handler,
        IReadOnlyList<AmqpSymbol>? capabilities,
        CancellationToken cancellationToken)
    {
        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);

        return await AmqpTransactionCoordinator.AcceptAsync(request, handler, capabilities, cancellationToken);
    }

    /// <summary>
    /// Accepts a control link with a coordinator target that announces no capabilities.
    /// </summary>
    private static async Task<AmqpTransactionCoordinator> AcceptSilentCoordinatorAsync(
        IAmqpSession session,
        IAmqpTransactionHandler handler,
        CancellationToken cancellationToken)
    {
        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);
        IAmqpReceiver link = await request.AcceptReceiverAsync(new AmqpReceiverOptions { Target = new AmqpCoordinator() }, cancellationToken);

        return AmqpTransactionCoordinator.Create(link, handler);
    }

    /// <summary>
    /// Attaches a receiver that grants no credit until told to on the controller's session, and accepts it as a
    /// sender on the resource's.
    /// </summary>
    private static async Task<(IAmqpReceiver Receiver, IAmqpSender Sender)> AttachAcquiringLinkAsync(
        IAmqpSession controllerSession,
        IAmqpSession resourceSession,
        AmqpSenderOptions? senderOptions,
        CancellationToken cancellationToken)
    {
        Task<IAmqpSender> accept = AcceptSenderAsync(resourceSession, senderOptions, cancellationToken);
        IAmqpReceiver receiver = await controllerSession.AttachReceiverAsync(
            new AmqpReceiverOptions
            {
                Source = new AmqpSource { Address = "queue" },
                Target = new AmqpTarget { Address = "controller" },
                Prefetch = 0
            },
            cancellationToken);
        IAmqpSender sender = await accept;

        return (receiver, sender);
    }

    private static async Task<IAmqpSender> AcceptSenderAsync(IAmqpSession session, AmqpSenderOptions? options, CancellationToken cancellationToken)
    {
        IAmqpLinkRequest request = await session.AcceptLinkAsync(cancellationToken);

        return await request.AcceptSenderAsync(options, cancellationToken);
    }
}
