using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Tests for the transaction manager: sequence assignment, snapshot semantics under
/// ReadCommitted and Snapshot isolation, commit/rollback lifecycle, and abort
/// behavior when the commit record cannot be made durable (#850).
/// </summary>
public class TransactionManagerTests
{
    private static (ITransactionManager Manager, IVersionStore Versions, ILockManager Locks) CreateKernel()
    {
        var locks = LockManager.Create();
        var versions = VersionStore.CreateInMemory();
        var manager = TransactionManager.Create(TransactionLog.CreateInMemory(), locks, versions);
        return (manager, versions, locks);
    }

    private static byte[] Payload(string text) => Encoding.UTF8.GetBytes(text);

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Begin: Should assign increasing sequences and active state")]
    public async Task Begin_MultipleTransactions_ShouldAssignIncreasingSequences()
    {
        // Arrange
        var (manager, _, _) = CreateKernel();
        await using var _ = manager;

        // Act
        var first = await manager.BeginAsync();
        var second = await manager.BeginAsync();

        // Assert
        second.Sequence.ShouldBeGreaterThan(first.Sequence);
        first.State.ShouldBe(TransactionState.Active);
        first.Snapshot.Owner.ShouldBe(first.Sequence);
        first.Id.ShouldNotBe(second.Id);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Snapshot isolation: Should not see writes committed after begin")]
    public async Task Snapshot_ConcurrentCommit_ShouldStayInvisibleToOlderSnapshot()
    {
        // Arrange
        var (manager, versions, _) = CreateKernel();
        await using var _ = manager;

        var reader = await manager.BeginAsync(IsolationLevel.Snapshot);

        var writer = await manager.BeginAsync();
        await versions.AppendVersionAsync(1, 1, Payload("late-write"), writer.Sequence);
        await manager.CommitAsync(writer);

        // Act
        var visibleToReader = await versions.GetVisibleVersionAsync(1, 1, reader.Snapshot);
        var newcomer = await manager.BeginAsync();
        var visibleToNewcomer = await versions.GetVisibleVersionAsync(1, 1, newcomer.Snapshot);

        // Assert: the older snapshot cannot see the write; a fresh snapshot can.
        visibleToReader.ShouldBeNull();
        visibleToNewcomer.ShouldNotBeNull();
        Encoding.UTF8.GetString(visibleToNewcomer!.Value.Span).ShouldBe("late-write");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - ReadCommitted: Should see writes committed after begin on refresh")]
    public async Task ReadCommitted_ConcurrentCommit_ShouldBecomeVisibleOnRefresh()
    {
        // Arrange
        var (manager, versions, _) = CreateKernel();
        await using var _ = manager;

        var reader = await manager.BeginAsync(IsolationLevel.ReadCommitted);
        (await versions.GetVisibleVersionAsync(1, 1, reader.Snapshot)).ShouldBeNull();

        var writer = await manager.BeginAsync();
        await versions.AppendVersionAsync(1, 1, Payload("committed-later"), writer.Sequence);
        await manager.CommitAsync(writer);

        // Act: the ReadCommitted snapshot refreshes per statement (per access).
        var visible = await versions.GetVisibleVersionAsync(1, 1, reader.Snapshot);

        // Assert
        visible.ShouldNotBeNull();
        Encoding.UTF8.GetString(visible!.Value.Span).ShouldBe("committed-later");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Own writes: Should always be visible to the writer")]
    public async Task Snapshot_OwnWrites_ShouldBeVisible()
    {
        // Arrange
        var (manager, versions, _) = CreateKernel();
        await using var _ = manager;

        var transaction = await manager.BeginAsync();
        await versions.AppendVersionAsync(7, 9, Payload("mine"), transaction.Sequence);

        // Act / Assert
        var visible = await versions.GetVisibleVersionAsync(7, 9, transaction.Snapshot);
        visible.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Rollback: Should purge the transaction's versions")]
    public async Task Rollback_WithVersions_ShouldPurgeWriterVersions()
    {
        // Arrange
        var (manager, versions, _) = CreateKernel();
        await using var _ = manager;

        var transaction = await manager.BeginAsync();
        await versions.AppendVersionAsync(1, 2, Payload("discarded"), transaction.Sequence);

        // Act
        await manager.RollbackAsync(transaction);

        // Assert: even a brand-new snapshot (which would see the sequence as
        // decided) finds nothing — the versions are gone, not just invisible.
        transaction.State.ShouldBe(TransactionState.RolledBack);
        var newcomer = await manager.BeginAsync();
        (await versions.GetVisibleVersionAsync(1, 2, newcomer.Snapshot)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Commit: Should release locks for waiting transactions")]
    public async Task Commit_HeldLocks_ShouldReleaseForWaiters()
    {
        // Arrange
        var (manager, _, locks) = CreateKernel();
        await using var _ = manager;

        var first = await manager.BeginAsync();
        var second = await manager.BeginAsync();

        var resource = LockResource.Entry(1, 42);
        await locks.AcquireAsync(first.Sequence, resource, LockMode.Exclusive);

        var waitingAcquire = locks.AcquireAsync(second.Sequence, resource, LockMode.Exclusive).AsTask();
        waitingAcquire.IsCompleted.ShouldBeFalse();

        // Act
        await manager.CommitAsync(first);

        // Assert
        await waitingAcquire.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Commit: Durability failure aborts with TransactionAbortedException")]
    public async Task Commit_LogFailure_ShouldAbortTransaction()
    {
        // Arrange
        var versions = VersionStore.CreateInMemory();
        var manager = TransactionManager.Create(new FailingCommitLog(), LockManager.Create(), versions);
        await using var _ = manager;

        var transaction = await manager.BeginAsync();
        await versions.AppendVersionAsync(3, 3, Payload("never-durable"), transaction.Sequence);

        // Act / Assert
        await Should.ThrowAsync<TransactionAbortedException>(async () => await manager.CommitAsync(transaction));
        transaction.State.ShouldBe(TransactionState.Faulted);

        // The failed transaction's versions were purged.
        var probe = await manager.BeginAsync();
        (await versions.GetVisibleVersionAsync(3, 3, probe.Snapshot)).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Lifecycle: Completed transactions reject further lifecycle calls")]
    public async Task Commit_CompletedTransaction_ShouldThrow()
    {
        // Arrange
        var (manager, _, _) = CreateKernel();
        await using var _ = manager;

        var transaction = await manager.BeginAsync();
        await manager.CommitAsync(transaction);

        // Act / Assert
        await Should.ThrowAsync<TransactionAbortedException>(async () => await manager.CommitAsync(transaction));
        await Should.ThrowAsync<TransactionAbortedException>(async () => await manager.RollbackAsync(transaction));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - OldestActive: Should track the oldest in-flight sequence")]
    public async Task OldestActive_MixedLifecycles_ShouldTrackOldestInFlight()
    {
        // Arrange
        var (manager, _, _) = CreateKernel();
        await using var _ = manager;

        var first = await manager.BeginAsync();
        var second = await manager.BeginAsync();

        // Act / Assert
        manager.OldestActive.ShouldBe(first.Sequence);

        await manager.CommitAsync(first);
        manager.OldestActive.ShouldBe(second.Sequence);

        await manager.CommitAsync(second);
        manager.OldestActive.ShouldBeGreaterThan(second.Sequence);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Sequence allocator: An external allocator assigns sequences and snapshots stay correct")]
    public async Task Begin_WithExternalSequenceAllocator_ShouldAssignFromAllocatorAndKeepSnapshotsCorrect()
    {
        // Arrange: an external counter standing in for a storage's sequence space,
        // pre-advanced past values the manager never saw (a storage-side bracket).
        ulong counter = 10;
        var manager = TransactionManager.Create(
            TransactionLog.CreateInMemory(),
            LockManager.Create(),
            VersionStore.CreateInMemory(),
            () => new TransactionSequence(++counter));
        await using var _ = manager;

        // Act
        var first = await manager.BeginAsync();
        var second = await manager.BeginAsync();

        // Assert: sequences come from the allocator, and the second snapshot
        // treats the first as in-flight (invisible) while admitting everything
        // the allocator handed out before the manager existed.
        first.Sequence.Value.ShouldBe(11UL);
        second.Sequence.Value.ShouldBe(12UL);
        second.Snapshot.IsVisible(first.Sequence).ShouldBeFalse();
        second.Snapshot.IsVisible(new TransactionSequence(5)).ShouldBeTrue();

        await manager.CommitAsync(first);
        await manager.CommitAsync(second);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Ownership: A context from another manager is rejected as aborted")]
    public async Task Commit_ContextFromAnotherManager_ShouldThrowTransactionAborted()
    {
        // Arrange
        var (managerA, _, _) = CreateKernel();
        var (managerB, _, _) = CreateKernel();
        await using var _ = managerA;
        await using var __ = managerB;

        var foreign = await managerB.BeginAsync();

        // Act + Assert
        await Should.ThrowAsync<TransactionAbortedException>(async () =>
            await managerA.CommitAsync(foreign));

        await managerB.RollbackAsync(foreign);
    }

    private sealed class FailingCommitLog : ITransactionLog
    {
        public ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default) => default;

        public ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated durability failure.");

        public ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default) => default;
    }
}
