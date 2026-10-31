using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection("Savepoint engine races")]
public sealed class SavepointMergeLockTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Matched marked MERGE uses no WAL, undo bracket or durability wait even with fsync failure armed (#1726)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_ReadOnlyMerge_ShouldRemainMemoryOnly(bool undo, bool failFsync)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = GraphDatabaseEngine.Create("memory-merge", new() { StorageStrategy = strategy,
            CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1), MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("MERGE (:Item {id: 1})");
        long journal = database.DataStorage.WriteAheadJournal.Length;
        int brackets = 0, flushes = 0;
        SetTransactionCommitting(database.DataStorage, _ => brackets++);
        FaultInjectingJournalStorageStrategy.BeforeCommitFlush = () => flushes++;
        using var failure = failFsync ? FaultInjectingJournalStorageStrategy.FailJournalFlushes(1) : null;

        // Act
        try
        {
            await session.ExecuteAsync("BEGIN");
            await session.ExecuteAsync("SAVEPOINT scope");
            await session.ExecuteAsync("MERGE (:Item {id: 1})");
            if (undo) { await session.ExecuteAsync("ROLLBACK TO scope"); }
            await session.ExecuteAsync("COMMIT");
        }
        finally { SetTransactionCommitting(database.DataStorage, null); FaultInjectingJournalStorageStrategy.BeforeCommitFlush = null; }

        // Assert
        database.DataStorage.WriteAheadJournal.Length.ShouldBe(journal);
        brackets.ShouldBe(0);
        flushes.ShouldBe(0);
        if (failure is not null) { failure.Attempts.ShouldBe(0); failure.Remaining.ShouldBe(1); }
        database.IsOffline.ShouldBeFalse();
        session.State.ShouldBe(SessionState.Open);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Marked MERGE bookkeeping drains before rollback or disposal removes its ledger (#1726)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireAsync_MarkedTrackerRacesEnd_ShouldDrainAndLeaveNoLateLedger(bool dispose)
    {
        // Arrange: a matched MERGE needs no row write; its lock bookkeeping must itself be
        // admitted before an end can remove the writer's ledger.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create("merge-ledger-end", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("MERGE (:Item {id: 1})", cancellationToken: timeout.Token);
        await session.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
        await session.ExecuteAsync("SAVEPOINT scope", cancellationToken: timeout.Token);
        await session.ExecuteAsync("MERGE (:Item {id: 1})", cancellationToken: timeout.Token);
        await session.ExecuteAsync("ROLLBACK TO scope", cancellationToken: timeout.Token);
        session.CurrentTransaction!.MergeLocks.RetainedBytes.ShouldBe(0);
        await session.ExecuteAsync("ROLLBACK", cancellationToken: timeout.Token);
        var resource = new GraphMergePatternIdentity([new(null, ["Item"], new Dictionary<string, object?> { ["id"] = 1L })], []).Resource;
        int rounds = 0, admitted = 0, pendingEnds = 0;
        var elapsed = Stopwatch.StartNew();

        // Act: recorded progress inside the marked bookkeeping bracket opens each end race.
        do
        {
            var transaction = await session.BeginTransactionAsync(timeout.Token);
            await session.ExecuteAsync("SAVEPOINT scope", cancellationToken: timeout.Token);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            int armed = 0;
            SetTrackedChangeObserver(null, () =>
            {
                if (Interlocked.CompareExchange(ref armed, 1, 0) == 0)
                {
                    Interlocked.Increment(ref admitted);
                    entered.TrySetResult();
                    release.Wait(timeout.Token);
                }
            });
            var acquiring = DedicatedTestTask.Run(async () =>
            {
                await database.RunAsync(session, async operation =>
                {
                    await operation.Merge.Locks.AcquireAsync(database, operation, resource, timeout.Token);
                    return true;
                }, timeout.Token);
            }, timeout.Token);
            Task? ending = null;
            Exception? failure = null;
            try
            {
                await entered.Task.WaitAsync(timeout.Token);
                ending = dispose ? transaction.DisposeAsync().AsTask() : transaction.RollbackAsync(timeout.Token).AsTask();
                // WaitForAppliesAsync publishes this signal only after an end has claimed
                // the lifecycle and observed a nonzero admission count. A mere yield is insufficient.
                Drained(transaction.Context).ShouldBeOfType<TaskCompletionSource>("the end must actually wait for admitted memory bookkeeping");
                ending.IsCompleted.ShouldBeFalse();
                pendingEnds++;
            }
            finally
            {
                release.Set();
                SetTrackedChangeObserver(null, null);
                try { failure = await Record.ExceptionAsync(() => acquiring); }
                finally
                {
                    if (ending is not null) { await ending; }
                    else { await transaction.RollbackAsync(); }
                }
            }
            failure.ShouldBeAssignableTo<DatabaseException>();
            transaction.State.ShouldBe(TransactionState.RolledBack);
            database.Coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
            rounds++;
        }
        while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || rounds < 8 || admitted < 8 || pendingEnds < 8)
            && elapsed.Elapsed < TimeSpan.FromSeconds(30));

        // Assert
        rounds.ShouldBeGreaterThanOrEqualTo(8);
        admitted.ShouldBe(rounds);
        pendingEnds.ShouldBe(rounds);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MERGE rollback restores retained lock charges, release transfers them and later MERGE reacquires (#1726)")]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 130)]
    public async Task ExecuteAsync_MergeUndo_ShouldRestoreTrackerAndReacquire(int prefix, int suffix)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<DatabaseResult> Execute(GraphDatabaseSession target, string text) => await target.ExecuteAsync(text, cancellationToken: timeout.Token);
        await using var engine = GraphDatabaseEngine.Create("merge-undo", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var peer = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "BEGIN");
        if (prefix != 0) { await Execute(session, "MERGE (:Item {id: 0})"); }
        var transaction = session.CurrentTransaction!;
        long before = transaction.MergeLocks.RetainedBytes;
        await Execute(session, "SAVEPOINT outer");
        await Execute(session, "SAVEPOINT child");
        for (int i = 1; i <= suffix; i++) { await Execute(session, $"MERGE (:Item {{id: {i}}})"); }
        long retained = transaction.MergeLocks.RetainedBytes;
        retained.ShouldBeGreaterThan(before);
        await Execute(session, "RELEASE child");
        transaction.MergeLocks.RetainedBytes.ShouldBe(retained);

        // Act
        await Execute(session, "ROLLBACK TO outer");

        // Assert
        transaction.MergeLocks.RetainedBytes.ShouldBe(before);
        await Execute(peer, "BEGIN");
        var resource = new GraphMergePatternIdentity([new(null, ["Item"], new Dictionary<string, object?> { ["id"] = 1L })], []).Resource;
        database.Coordinator.LockManager.TryAcquire(peer.CurrentTransaction!.Context.Sequence, resource, LockMode.Exclusive).ShouldBeTrue();
        await Execute(peer, "ROLLBACK");
        await Execute(peer, "BEGIN");
        await Execute(session, "MERGE (:Item {id: 1})");
        database.Coordinator.LockManager.TryAcquire(peer.CurrentTransaction.Context.Sequence, resource, LockMode.Exclusive).ShouldBeFalse("MERGE must take its released grant again");
        await Execute(session, "MERGE (:Item {id: 1})");
        await Execute(session, "COMMIT");
        await Execute(peer, "ROLLBACK");
        await using var result = (await Execute(session, "MATCH (n:Item) RETURN count(*)")).ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in result.GetRowsAsync()) { Convert.ToInt64(row.GetValue(0)).ShouldBe(prefix + 1); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Repeated rolled-back MERGEs do not exhaust a bounded statement memory budget (#1726)")]
    public async Task ExecuteAsync_MergeUndoAtMemoryLimit_ShouldAvoidSpuriousRefusal()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task<DatabaseResult> Execute(GraphDatabaseSession target, string text) => await target.ExecuteAsync(text, cancellationToken: timeout.Token);
        await using var engine = GraphDatabaseEngine.Create("merge-budget-undo", new() { QueryMemoryBudget = 16_384 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "BEGIN");
        await Execute(session, "SAVEPOINT scope");

        // Act / Assert
        for (int i = 0; i < 80; i++)
        {
            await Execute(session, $"MERGE (:Item {{id: {i}}})");
            await Execute(session, "ROLLBACK TO scope");
        }
        session.CurrentTransaction!.MergeLocks.RetainedBytes.ShouldBe(0);
        await Execute(session, "MERGE (:Item {id: 100})");
        await Execute(session, "COMMIT");
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "set_BeforeTrackedChange")]
    private static extern void SetTrackedChangeObserver(GraphMergeLocks? type, Action? observer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_drained")]
    private static extern ref object? Drained(TransactionContext context);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_TransactionCommitting")]
    private static extern void SetTransactionCommitting(global::Assimalign.Cohesion.Database.Storage.Storage storage, Action<StorageTransaction>? observer);
}
