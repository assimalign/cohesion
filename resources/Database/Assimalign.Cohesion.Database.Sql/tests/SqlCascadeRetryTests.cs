using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlCascadeRetryTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Cascade: waiting for a row or unique key releases new closure locks and preserves earlier grants (#1420)")]
    [InlineData("CASCADE", false)]
    [InlineData("CASCADE", true)]
    [InlineData("SET NULL", false)]
    [InlineData("SET NULL", true)]
    [InlineData("SET DEFAULT", false)]
    [InlineData("SET DEFAULT", true)]
    public Task ExecuteAsync_PreparationLockRefused_ShouldReleaseNewParentGrantBeforeWaiting(string action, bool uniqueKey)
        => VerifyPreparationRetryAsync(action, uniqueKey);

    [Fact(DisplayName = "Cohesion Test [Sql] - Cascade: restarted preparation reacquires table intents before applying (#1420)")]
    public Task ExecuteAsync_AfterRetry_ShouldReacquireTableIntents()
        => VerifyPreparationRetryAsync("CASCADE", uniqueKey: false, inspectLocks: true);

    [Fact(DisplayName = "Cohesion Test [Sql] - Cascade: a refused released-reference parent lock backs off the whole closure (#1420)")]
    public Task ExecuteAsync_ReleasedReferenceParentRefused_ShouldReleaseClosureRows()
        => VerifyPreparationRetryAsync("CASCADE", uniqueKey: false, sharedParent: true);

    [Fact(DisplayName = "Cohesion Test [Sql] - Cascade: a refused adjacent-table intent backs off the whole closure (#1420)")]
    public Task ExecuteAsync_AdjacentTableIntentRefused_ShouldReleaseClosureRows()
        => VerifyPreparationRetryAsync("CASCADE", uniqueKey: false, sharedParent: true, tableIntent: true);

    [Fact(DisplayName = "Cohesion Test [Sql] - Cascade: retry refreshes adjacent parent intents before the physical apply (#1420)")]
    public Task ExecuteAsync_AdjacentIntentAfterRetry_ShouldStillExcludeDdl()
        => VerifyPreparationRetryAsync("CASCADE", uniqueKey: false, inspectLocks: true, sharedParent: true);

    private static async Task VerifyPreparationRetryAsync(string action, bool uniqueKey, bool inspectLocks = false,
        bool sharedParent = false, bool tableIntent = false)
    {
        // Arrange: direct kernel ownership parks the child without also holding its parent.
        // Thirty seconds is a hang guard; lock-wait events order every interleaving.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("cascade-retry", new());
        var database = await engine.CreateDatabaseAsync("retry-" + Guid.NewGuid().ToString("N"), timeout.Token);
        await using var setup = await database.CreateSessionAsync(timeout.Token);
        await setup.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        if (sharedParent)
        {
            await setup.ExecuteAsync("CREATE TABLE q(id INT PRIMARY KEY)", cancellationToken: timeout.Token);
            await setup.ExecuteAsync("INSERT INTO q VALUES(1)", cancellationToken: timeout.Token);
        }
        await setup.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id) ON DELETE " + action +
            (sharedParent ? ", qid INT REFERENCES q(id))" : ")"), cancellationToken: timeout.Token);
        await setup.ExecuteAsync("INSERT INTO p VALUES (1)", cancellationToken: timeout.Token);
        await setup.ExecuteAsync(sharedParent ? "INSERT INTO c VALUES(10,1,1)" : "INSERT INTO c VALUES(10,1)", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "p", out var parent).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "c", out var child).ShouldBeTrue();
        var coordinator = database.Coordinator;
        var locks = coordinator.LockManager;
        var holder = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        var probe = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        LockResource parentRow = FirstRow(database, parent.ObjectId);
        var key = new DatabaseKeyWriter();
        SqlRowCodec.AppendKeyValue(key, DatabaseType.Int32, 1, Collation.Binary);
        var blocked = uniqueKey ? LockResource.Entry(parent.ObjectId, IndexKey.From(key).Hash()) : FirstRow(database, child.ObjectId);
        ulong? adjacentObjectId = null;
        if (sharedParent)
        {
            database.Catalog.TryGetTable("dbo", "q", out var otherParent).ShouldBeTrue();
            adjacentObjectId = otherParent.ObjectId;
            blocked = tableIntent ? LockResource.Object(otherParent.ObjectId) : FirstRow(database, otherParent.ObjectId);
        }
        await locks.AcquireAsync(holder.Sequence, blocked, LockMode.Exclusive, timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        var earlier = LockResource.Object(0xFFFF_FF00);
        await locks.AcquireAsync(transaction.Context.Sequence, earlier, LockMode.Exclusive, timeout.Token);
        int prepared = 0;
        if (inspectLocks)
        {
            session.ResultPreparation = new SqlResultPreparation(() =>
            {
                // Runs inside the real physical apply bracket, after the retry.
                // IS would allow S, while the required target-table IX refuses it.
                var inspection = new TransactionSequence(ulong.MaxValue - 1420);
                try
                {
                    locks.TryAcquire(inspection, LockResource.Object(parent.ObjectId), LockMode.Shared).ShouldBeFalse();
                    if (adjacentObjectId is ulong adjacent)
                    {
                        // The row is shared-locked, but DDL needs the separate table intent.
                        locks.TryAcquire(inspection, LockResource.Object(adjacent), LockMode.Exclusive).ShouldBeFalse();
                    }
                }
                finally { locks.ReleaseAll(inspection); }
                prepared++;
                return new SqlQueryResult(DatabaseResultStatus.Success, 1);
            });
        }
        using var waits = new LockWaitListener(database.Name);
        Task<DatabaseResult>? deleting = null;
        DatabaseResult? result = null;
        try
        {
            // Act: the unapplied cascade reaches the held child.
            deleting = session.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: timeout.Token).AsTask();
            await waits.WaitForWaitAsync(transaction.Context.Sequence, timeout.Token);

            // Assert before releasing the child: an FK parent reader can now progress,
            // but a grant from an earlier statement remains protected.
            locks.TryAcquire(probe.Sequence, parentRow, LockMode.Shared).ShouldBeTrue();
            if (sharedParent) { locks.TryAcquire(probe.Sequence, FirstRow(database, child.ObjectId), LockMode.Exclusive).ShouldBeTrue(); }
            locks.TryAcquire(probe.Sequence, earlier, LockMode.Shared).ShouldBeFalse();
            deleting.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            await coordinator.RollbackAsync(probe, CancellationToken.None);
            await coordinator.RollbackAsync(holder, CancellationToken.None);
            if (deleting is not null) { result = await deleting.WaitAsync(TimeSpan.FromSeconds(30)); }
        }
        prepared.ShouldBe(inspectLocks ? 1 : 0);
        result.ShouldNotBeNull();
        if (!inspectLocks) { result.AffectedCount.ShouldBe(1); } // callback case replaces only the result object
        await transaction.CommitAsync(timeout.Token);
        foreach (string table in new[] { "p", "c" })
        {
            await using var rows = (DatabaseResultSet)await setup.ExecuteAsync("SELECT COUNT(*) FROM " + table, cancellationToken: timeout.Token);
            await foreach (var row in rows.GetRowsAsync(timeout.Token)) { Convert.ToInt64(row.GetValue(0)).ShouldBe(table == "c" && action != "CASCADE" ? 1L : 0L); }
        }
        if (sharedParent)
        {
            await using var rows = (DatabaseResultSet)await setup.ExecuteAsync("SELECT COUNT(*) FROM q", cancellationToken: timeout.Token);
            await foreach (var row in rows.GetRowsAsync(timeout.Token)) { Convert.ToInt64(row.GetValue(0)).ShouldBe(1L); }
        }
        if (action != "CASCADE")
        {
            await using var rows = (DatabaseResultSet)await setup.ExecuteAsync("SELECT COUNT(*) FROM c WHERE pid IS NULL", cancellationToken: timeout.Token);
            await foreach (var row in rows.GetRowsAsync(timeout.Token)) { Convert.ToInt64(row.GetValue(0)).ShouldBe(1L); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Cascade: a second back-off retains the first waited grant and cancellation releases only statement grants (#1420)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExecuteAsync_TwoRefusedChildren_ShouldKeepTheFirstWaitGrantAcrossTheSecondRetry(bool cancel, bool inspectLocks)
    {
        // Thirty seconds is only a hang guard; two actual wait-start events order the test.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("cascade-two-waits", new());
        var database = await engine.CreateDatabaseAsync("retry-" + Guid.NewGuid().ToString("N"), timeout.Token);
        await using var setup = await database.CreateSessionAsync(timeout.Token);
        await setup.ExecuteAsync("CREATE TABLE p(id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        await setup.ExecuteAsync("CREATE TABLE c(id INT PRIMARY KEY, pid INT REFERENCES p(id) ON DELETE CASCADE)", cancellationToken: timeout.Token);
        await setup.ExecuteAsync("INSERT INTO p VALUES(1)", cancellationToken: timeout.Token);
        await setup.ExecuteAsync("INSERT INTO c VALUES(10,1),(11,1)", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "p", out var parent).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "c", out var child).ShouldBeTrue();
        var children = new List<LockResource>();
        using (var iterator = database.DataStorage.GetUnitIterator(child.ObjectId))
        {
            while (iterator.MoveNext())
            {
                var unit = iterator.Current;
                children.Add(LockResource.Entry(child.ObjectId, SqlRecordLocation.Pack(unit.PageId, unit.SlotIndex)));
            }
        }
        children.Count.ShouldBe(2);
        var coordinator = database.Coordinator;
        var locks = coordinator.LockManager;
        var one = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        var two = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        var probe = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await locks.AcquireAsync(one.Sequence, children[0], LockMode.Exclusive, timeout.Token);
        await locks.AcquireAsync(two.Sequence, children[1], LockMode.Exclusive, timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        var earlier = LockResource.Object(0xFFFF_FF00);
        await locks.AcquireAsync(transaction.Context.Sequence, earlier, LockMode.Exclusive, timeout.Token);
        int prepared = 0;
        if (inspectLocks)
        {
            session.ResultPreparation = new SqlResultPreparation(() =>
            {
                var inspection = new TransactionSequence(ulong.MaxValue - 1420);
                try { locks.TryAcquire(inspection, LockResource.Object(child.ObjectId), LockMode.Shared).ShouldBeFalse(); }
                finally { locks.ReleaseAll(inspection); }
                prepared++;
                return new SqlQueryResult(DatabaseResultStatus.Success, 1);
            });
        }
        using var waits = new LockWaitListener(database.Name);
        var secondWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        waits.Observed = written =>
        {
            if (written.EventName == "LockWaitStart" && written.Payload is { Count: > 1 } payload &&
                Equals(payload[1], (long)transaction.Context.Sequence.Value) && Interlocked.Increment(ref starts) == 2)
            {
                secondWait.TrySetResult();
            }
        };
        bool oneReleased = false;
        Task<DatabaseResult>? deleting = null;
        DatabaseResult? result = null;
        try
        {
            deleting = session.ExecuteAsync("DELETE FROM p WHERE id=1", cancellationToken: timeout.Token).AsTask();
            await waits.WaitForWaitAsync(transaction.Context.Sequence, timeout.Token);
            await coordinator.RollbackAsync(one, CancellationToken.None);
            oneReleased = true;
            await secondWait.Task.WaitAsync(TimeSpan.FromSeconds(30));
            starts.ShouldBe(2);
            locks.TryAcquire(probe.Sequence, children[0], LockMode.Exclusive).ShouldBeFalse();
            locks.TryAcquire(probe.Sequence, FirstRow(database, parent.ObjectId), LockMode.Shared).ShouldBeTrue();
            locks.TryAcquire(probe.Sequence, earlier, LockMode.Shared).ShouldBeFalse();
            if (cancel)
            {
                timeout.Cancel();
                var canceled = await Should.ThrowAsync<OperationCanceledException>(async () => await deleting);
                canceled.CancellationToken.ShouldBe(timeout.Token);
                transaction.State.ShouldBe(TransactionState.Active);
                locks.TryAcquire(probe.Sequence, children[0], LockMode.Exclusive).ShouldBeTrue();
                locks.TryAcquire(probe.Sequence, earlier, LockMode.Shared).ShouldBeFalse();
            }
        }
        finally
        {
            waits.Observed = null;
            if (cancel) { timeout.Cancel(); }
            await coordinator.RollbackAsync(probe, CancellationToken.None);
            if (!oneReleased) { await coordinator.RollbackAsync(one, CancellationToken.None); }
            await coordinator.RollbackAsync(two, CancellationToken.None);
            if (deleting is not null)
            {
                if (cancel)
                {
                    try { await deleting.WaitAsync(TimeSpan.FromSeconds(30)); }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
                }
                else { result = await deleting.WaitAsync(TimeSpan.FromSeconds(30)); }
            }
        }
        if (cancel) { await transaction.RollbackAsync(CancellationToken.None); }
        else
        {
            prepared.ShouldBe(inspectLocks ? 1 : 0);
            result.ShouldNotBeNull();
            if (!inspectLocks) { result.AffectedCount.ShouldBe(1); }
            await transaction.CommitAsync(CancellationToken.None);
        }
        foreach (string table in new[] { "p", "c" })
        {
            await using var rows = (DatabaseResultSet)await setup.ExecuteAsync("SELECT COUNT(*) FROM " + table, cancellationToken: CancellationToken.None);
            long expected = cancel ? table == "p" ? 1 : 2 : 0;
            await foreach (var row in rows.GetRowsAsync(CancellationToken.None)) { Convert.ToInt64(row.GetValue(0)).ShouldBe(expected); }
        }
    }

    private static LockResource FirstRow(SqlDatabase database, ulong objectId)
    {
        using var iterator = database.DataStorage.GetUnitIterator(objectId);
        iterator.MoveNext().ShouldBeTrue();
        var unit = iterator.Current;
        return LockResource.Entry(objectId, SqlRecordLocation.Pack(unit.PageId, unit.SlotIndex));
    }
}
