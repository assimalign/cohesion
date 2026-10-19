using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

// Conformance cases for DIALECT's release/wait/re-arbitrate contract (#1668).
[Collection(SqlUpsertConcurrencyCollection.Name)]
public sealed class SqlUpsertArbitrationConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Upsert arbitration: gated row hand-off completes each increment exactly once")]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExecuteAsync_QueuedRowHandOff_ShouldKeepTheGrantedRow(bool crossArbiters)
        => RepeatAsync(async () =>
        {
            // Arrange: both waiters release their proposal keys before queuing behind a third owner.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var releaseFirst = new ManualResetEventSlim();
            using var releaseSecond = new ManualResetEventSlim();
            await using var engine = SqlDatabaseEngine.Create("upsert-hand-off", new());
            var database = await engine.CreateDatabaseAsync("app", timeout.Token);
            await using var first = await database.CreateSessionAsync(timeout.Token);
            await using var second = await database.CreateSessionAsync(timeout.Token);
            await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, email TEXT UNIQUE, v INT)", cancellationToken: timeout.Token);
            await first.ExecuteAsync("INSERT INTO t VALUES (1, 'mail', 0)", cancellationToken: timeout.Token);
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            var holder = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
            await HoldFirstRowAsync(database, table.ObjectId, holder.Sequence, timeout.Token);
            await using var one = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
            await using var two = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
            using var waits = new LockWaitListener(database.Name);
            var firstGranted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondGranted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int firstStops = 0;
            int secondStops = 0;
            waits.Observed = written =>
            {
                if (written.EventName != "LockWaitStop" || written.Payload is not { Count: > 2 } payload || !Equals(payload[2], "Granted")) { return; }
                long owner = (long)payload[1]!;
                if (owner == (long)one.Context.Sequence.Value && Interlocked.Increment(ref firstStops) == 1)
                {
                    firstGranted.SetResult();
                    releaseFirst.Wait(timeout.Token);
                }
                if (owner == (long)two.Context.Sequence.Value && Interlocked.Increment(ref secondStops) == 1)
                {
                    secondGranted.SetResult();
                    releaseSecond.Wait(timeout.Token);
                }
            };
            // Cross-arbiter proposals have disjoint key sets: (1, 'other') and (2, 'mail').
            // Each reaches the shared target through a different unique index.
            string firstSql = "INSERT INTO t VALUES (1, '" + (crossArbiters ? "other" : "mail") +
                "', 1) ON CONFLICT(id) DO UPDATE SET v = t.v + EXCLUDED.v RETURNING v";
            string secondSql = "INSERT INTO t VALUES (" + (crossArbiters ? "2" : "1") +
                ", 'mail', 2) ON CONFLICT(" + (crossArbiters ? "email" : "id") + ") DO UPDATE SET v = t.v + EXCLUDED.v RETURNING v";
            if (crossArbiters)
            {
                new[] { Key(1).Hash(), Key("other").Hash() }.Intersect(new[] { Key(2).Hash(), Key("mail").Hash() }).ShouldBeEmpty();
            }
            var pendingOne = first.ExecuteAsync(firstSql, cancellationToken: timeout.Token).AsTask();
            await waits.WaitForWaitAsync(one.Context.Sequence, timeout.Token);
            var pendingTwo = second.ExecuteAsync(secondSql, cancellationToken: timeout.Token).AsTask();
            try
            {
                await waits.WaitForWaitAsync(two.Context.Sequence, timeout.Token);

                // Act: suspend A at its completed wait, then let A restart while B remains gated.
                await database.Coordinator.RollbackAsync(holder, timeout.Token);
                await firstGranted.Task.WaitAsync(timeout.Token);
                pendingOne.IsCompleted.ShouldBeFalse();
                pendingTwo.IsCompleted.ShouldBeFalse();
                secondStops.ShouldBe(0);
                releaseFirst.Set();
                await using var firstResult = (await pendingOne.WaitAsync(timeout.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
                firstResult.AffectedCount.ShouldBe(1);
                (await RowsAsync(firstResult)).ShouldHaveSingleItem()[0].ShouldBe(1);
                pendingTwo.IsCompleted.ShouldBeFalse();
                secondStops.ShouldBe(0);
                await one.CommitAsync(timeout.Token);
                await secondGranted.Task.WaitAsync(timeout.Token);
                releaseSecond.Set();
                await using var secondResult = (await pendingTwo.WaitAsync(timeout.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
                secondResult.AffectedCount.ShouldBe(1);
                (await RowsAsync(secondResult)).ShouldHaveSingleItem()[0].ShouldBe(3);
                await two.CommitAsync(timeout.Token);

                // Assert: the same-key and disjoint-key schedules both apply each statement once.
                firstStops.ShouldBe(1);
                secondStops.ShouldBe(1);
                (await QueryAsync(first, "SELECT id, email, v FROM t")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, "mail", 3 });
            }
            finally
            {
                releaseFirst.Set();
                releaseSecond.Set();
                timeout.Cancel();
                if (holder.State == TransactionState.Active) { await database.Coordinator.RollbackAsync(holder, CancellationToken.None); }
                Task completed = Task.WhenAll(pendingOne, pendingTwo);
                await completed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        });

    [Fact(DisplayName = "Cohesion Test [Sql] - Upsert arbitration: disjoint cross-arbiter keys and opposite rows detect a victim and permit retry")]
    public Task ExecuteAsync_CrossArbitersOppositeRows_ShouldDetectDeadlockAndPermitRetry()
        => RepeatAsync(async () =>
        {
            // Arrange: A arbitrates id in row order 1,2; B arbitrates email in row order 2,1.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var releaseFirst = new ManualResetEventSlim();
            using var releaseSecond = new ManualResetEventSlim();
            await using var engine = SqlDatabaseEngine.Create("upsert-cross-rows", new());
            var database = await engine.CreateDatabaseAsync("app", timeout.Token);
            await using var first = await database.CreateSessionAsync(timeout.Token);
            await using var second = await database.CreateSessionAsync(timeout.Token);
            await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, email TEXT UNIQUE, v INT)", cancellationToken: timeout.Token);
            await first.ExecuteAsync("INSERT INTO t VALUES (1, 'one', 0), (2, 'two', 0)", cancellationToken: timeout.Token);
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            var locks = database.Coordinator.LockManager;
            var holderOne = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
            var holderTwo = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
            await HoldFirstRowAsync(database, table.ObjectId, holderOne.Sequence, timeout.Token);
            using (var iterator = database.DataStorage.GetUnitIterator(table.ObjectId))
            {
                iterator.MoveNext().ShouldBeTrue();
                iterator.MoveNext().ShouldBeTrue();
                var unit = iterator.Current;
                await locks.AcquireAsync(holderTwo.Sequence, LockResource.Entry(table.ObjectId,
                    SqlRecordLocation.Pack(unit.PageId, unit.SlotIndex)), LockMode.Exclusive, timeout.Token);
            }
            await using var one = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
            await using var two = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
            using var waits = new LockWaitListener(database.Name);
            var firstGranted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondGranted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstBlockedAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int firstStarts = 0;
            int firstStops = 0;
            int secondStops = 0;
            waits.Observed = written =>
            {
                if (written.Payload is not { Count: > 2 } payload) { return; }
                long owner = (long)payload[1]!;
                if (written.EventName == "LockWaitStart" && owner == (long)one.Context.Sequence.Value && Interlocked.Increment(ref firstStarts) == 2)
                { firstBlockedAgain.SetResult(); }
                if (written.EventName != "LockWaitStop" || !Equals(payload[2], "Granted")) { return; }
                if (owner == (long)one.Context.Sequence.Value && Interlocked.Increment(ref firstStops) == 1)
                { firstGranted.SetResult(); releaseFirst.Wait(timeout.Token); }
                if (owner == (long)two.Context.Sequence.Value && Interlocked.Increment(ref secondStops) == 1)
                { secondGranted.SetResult(); releaseSecond.Wait(timeout.Token); }
            };
            // Both batches are disjoint; their first row grants, rather than keys, conflict.
            const string firstSql = "INSERT INTO t VALUES (1, 'a', 1), (2, 'b', 1) ON CONFLICT(id) DO UPDATE SET v = t.v + EXCLUDED.v";
            const string secondSql = "INSERT INTO t VALUES (3, 'two', 2), (4, 'one', 2) ON CONFLICT(email) DO UPDATE SET v = t.v + EXCLUDED.v";
            new[] { Key(1).Hash(), Key("a").Hash(), Key(2).Hash(), Key("b").Hash() }
                .Intersect(new[] { Key(3).Hash(), Key("two").Hash(), Key(4).Hash(), Key("one").Hash() }).ShouldBeEmpty();
            var pendingOne = first.ExecuteAsync(firstSql, cancellationToken: timeout.Token).AsTask();
            await waits.WaitForWaitAsync(one.Context.Sequence, timeout.Token);
            var pendingTwo = second.ExecuteAsync(secondSql, cancellationToken: timeout.Token).AsTask();
            try
            {
                await waits.WaitForWaitAsync(two.Context.Sequence, timeout.Token);
                await database.Coordinator.RollbackAsync(holderOne, timeout.Token);
                await database.Coordinator.RollbackAsync(holderTwo, timeout.Token);
                await Task.WhenAll(firstGranted.Task, secondGranted.Task).WaitAsync(timeout.Token);

                // Act: keep A's first row while it waits for B's, then let B close the cycle.
                releaseFirst.Set();
                await firstBlockedAgain.Task.WaitAsync(timeout.Token);
                releaseSecond.Set();
                await Should.ThrowAsync<DatabaseTransactionDeadlockException>(async () => await pendingTwo);
                await two.RollbackAsync(timeout.Token);
                (await pendingOne).AffectedCount.ShouldBe(2);
                await one.CommitAsync(timeout.Token);
                await using var retry = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
                (await second.ExecuteAsync(secondSql, cancellationToken: timeout.Token)).AffectedCount.ShouldBe(2);
                await retry.CommitAsync(timeout.Token);

                // Assert: the failed preparation applied nothing; each successful statement applied once.
                firstStarts.ShouldBe(2);
                firstStops.ShouldBe(2);
                secondStops.ShouldBe(1);
                (await QueryAsync(first, "SELECT v FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 3, 3 });
            }
            finally
            {
                releaseFirst.Set();
                releaseSecond.Set();
                timeout.Cancel();
                if (holderOne.State == TransactionState.Active) { await database.Coordinator.RollbackAsync(holderOne, CancellationToken.None); }
                if (holderTwo.State == TransactionState.Active) { await database.Coordinator.RollbackAsync(holderTwo, CancellationToken.None); }
                Task completed = Task.WhenAll(pendingOne, pendingTwo);
                await completed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        });

    [Fact(DisplayName = "Cohesion Test [Sql] - Upsert arbitration: a pre-statement shared key retains its exclusive upgrade through restart")]
    public Task ExecuteAsync_PreStatementSharedKey_ShouldKeepItsUpgrade()
        => RepeatAsync(async () =>
        {
            // Arrange
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var engine = SqlDatabaseEngine.Create("upsert-upgrade", new());
            var database = await engine.CreateDatabaseAsync("app", timeout.Token);
            await using var writer = await database.CreateSessionAsync(timeout.Token);
            await using var contender = await database.CreateSessionAsync(timeout.Token);
            await writer.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
            await writer.ExecuteAsync("INSERT INTO t VALUES (1, 10)", cancellationToken: timeout.Token);
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            await using var holder = await writer.BeginTransactionAsync(timeout.Token);
            await HoldFirstRowAsync(database, table.ObjectId, holder.Context.Sequence, timeout.Token);
            await using var upsert = await contender.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
            var locks = database.Coordinator.LockManager;
            var key = LockResource.Entry(table.ObjectId, Key(1).Hash());
            locks.TryAcquire(upsert.Context.Sequence, key, LockMode.Shared).ShouldBeTrue();
            var probe = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
            using var waits = new LockWaitListener(database.Name);
            try
            {
                // Act: SQL upgrades the key to X, then backs off on the held row.
                var pending = contender.ExecuteAsync("INSERT INTO t VALUES (1, 20) ON CONFLICT(id) DO UPDATE SET v = EXCLUDED.v", cancellationToken: timeout.Token).AsTask();
                await waits.WaitForWaitAsync(upsert.Context.Sequence, timeout.Token);

                // Assert: a shared probe would succeed if only S survived the back-off.
                locks.TryAcquire(probe.Sequence, key, LockMode.Shared).ShouldBeFalse();
                await holder.RollbackAsync(timeout.Token);
                (await pending).AffectedCount.ShouldBe(1);
                locks.TryAcquire(probe.Sequence, key, LockMode.Shared).ShouldBeFalse();
                await upsert.CommitAsync(timeout.Token);
                locks.TryAcquire(probe.Sequence, key, LockMode.Shared).ShouldBeTrue();
                (await QueryAsync(writer, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(20);
            }
            finally { await database.Coordinator.RollbackAsync(probe, CancellationToken.None); }
        });

    [Theory(DisplayName = "Cohesion Test [Sql] - DO NOTHING: an existing parent takes no row lock and leaves concurrent writers unblocked")]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExecuteAsync_DoNothingExistingParent_ShouldAllowConcurrentWrites(bool childInsert)
        => RepeatAsync(async () =>
        {
            // Arrange: holding the parent row must not make DO NOTHING wait for it.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var engine = SqlDatabaseEngine.Create("upsert-nothing-lock", new());
            var database = await engine.CreateDatabaseAsync("app", timeout.Token);
            await using var first = await database.CreateSessionAsync(timeout.Token);
            await using var second = await database.CreateSessionAsync(timeout.Token);
            await first.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
            await first.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id))", cancellationToken: timeout.Token);
            await first.ExecuteAsync("INSERT INTO p VALUES (1, 0)", cancellationToken: timeout.Token);
            database.Catalog.TryGetTable("dbo", "p", out var table).ShouldBeTrue();
            await using var holder = await second.BeginTransactionAsync(timeout.Token);
            await HoldFirstRowAsync(database, table.ObjectId, holder.Context.Sequence, timeout.Token);
            await using var noOp = await first.BeginTransactionAsync(timeout.Token);

            // Act: keep the no-op transaction open throughout the concurrent SQL write.
            (await first.ExecuteAsync("INSERT INTO p VALUES (1, 99) ON CONFLICT DO NOTHING", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(0);
            await holder.RollbackAsync(timeout.Token);
            (await second.ExecuteAsync(childInsert ? "INSERT INTO c VALUES (1, 1)" : "UPDATE p SET v = v + 1 WHERE id = 1",
                cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);

            // Assert
            noOp.State.ShouldBe(TransactionState.Active);
            await noOp.CommitAsync(timeout.Token);
            (await QueryAsync(first, "SELECT v FROM p")).ShouldHaveSingleItem()[0].ShouldBe(childInsert ? 0 : 1);
            (await QueryAsync(first, "SELECT id FROM c")).Count.ShouldBe(childInsert ? 1 : 0);
        });

    [Fact(DisplayName = "Cohesion Test [Sql] - DO NOTHING: Snapshot rejects a newer committed conflict without waiting for its row")]
    public Task ExecuteAsync_DoNothingNewerCommittedRow_ShouldAbortByVisibility()
        => RepeatAsync(async () =>
        {
            // Arrange: the snapshot predates the committed row, which another owner now locks.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var engine = SqlDatabaseEngine.Create("upsert-nothing-snapshot", new());
            var database = await engine.CreateDatabaseAsync("app", timeout.Token);
            await using var first = await database.CreateSessionAsync(timeout.Token);
            await using var second = await database.CreateSessionAsync(timeout.Token);
            await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY)", cancellationToken: timeout.Token);
            await using var old = await first.BeginTransactionAsync(IsolationLevel.Snapshot, timeout.Token);
            await second.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: timeout.Token);
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            await using var holder = await second.BeginTransactionAsync(timeout.Token);
            await HoldFirstRowAsync(database, table.ObjectId, holder.Context.Sequence, timeout.Token);

            // Act / Assert: locking first would stall behind holder instead of reporting visibility.
            await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await first.ExecuteAsync(
                "INSERT INTO t VALUES (1) ON CONFLICT DO NOTHING", cancellationToken: timeout.Token));
            await holder.RollbackAsync(timeout.Token);
            await old.RollbackAsync(timeout.Token);
            (await QueryAsync(second, "SELECT id FROM t")).ShouldHaveSingleItem()[0].ShouldBe(1);
        });

    [Fact(DisplayName = "Cohesion Test [Sql] - DO UPDATE: a false filter keeps the conflicting row locked and unchanged")]
    public Task ExecuteAsync_FalseUpdateFilter_ShouldRetainTheRowLock()
        => RepeatAsync(async () =>
        {
            // Arrange
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var engine = SqlDatabaseEngine.Create("upsert-filter-lock", new());
            var database = await engine.CreateDatabaseAsync("app", timeout.Token);
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
            await session.ExecuteAsync("INSERT INTO t VALUES (1, 10)", cancellationToken: timeout.Token);
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            await using var upsert = await session.BeginTransactionAsync(timeout.Token);
            var probe = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
            try
            {
                // Act
                (await session.ExecuteAsync("INSERT INTO t VALUES (1, 99) ON CONFLICT(id) DO UPDATE SET v = EXCLUDED.v WHERE false",
                    cancellationToken: timeout.Token)).AffectedCount.ShouldBe(0);

                // Assert: the filter does not release the X row lock it acquired before evaluation.
                using var iterator = database.DataStorage.GetUnitIterator(table.ObjectId);
                iterator.MoveNext().ShouldBeTrue();
                var unit = iterator.Current;
                var row = LockResource.Entry(table.ObjectId, SqlRecordLocation.Pack(unit.PageId, unit.SlotIndex));
                database.Coordinator.LockManager.TryAcquire(probe.Sequence, row, LockMode.Shared).ShouldBeFalse();
                (await QueryAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(10);
                await upsert.CommitAsync(timeout.Token);
                database.Coordinator.LockManager.TryAcquire(probe.Sequence, row, LockMode.Shared).ShouldBeTrue();
            }
            finally { await database.Coordinator.RollbackAsync(probe, CancellationToken.None); }
        });

    [Theory(DisplayName = "Cohesion Test [Sql] - Upsert arbitration: row-first rekeys and deletes complete or serialize by isolation")]
    [InlineData(IsolationLevel.ReadCommitted, false, false)]
    [InlineData(IsolationLevel.ReadCommitted, true, false)]
    [InlineData(IsolationLevel.Snapshot, false, false)]
    [InlineData(IsolationLevel.Snapshot, true, false)]
    [InlineData(IsolationLevel.ReadCommitted, false, true)]
    [InlineData(IsolationLevel.ReadCommitted, true, true)]
    [InlineData(IsolationLevel.Snapshot, false, true)]
    [InlineData(IsolationLevel.Snapshot, true, true)]
    public Task ExecuteAsync_RowFirstWriter_ShouldRestartArbitration(IsolationLevel isolation, bool delete, bool rollback)
        => RepeatAsync(async () =>
        {
            // Arrange: the writer owns the row before requesting either unique key.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var engine = SqlDatabaseEngine.Create("upsert-row-first", new());
            var database = await engine.CreateDatabaseAsync("app", timeout.Token);
            await using var writer = await database.CreateSessionAsync(timeout.Token);
            await using var contender = await database.CreateSessionAsync(timeout.Token);
            await writer.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
            await writer.ExecuteAsync("INSERT INTO t VALUES (1, 10)", cancellationToken: timeout.Token);
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            await using var holder = await writer.BeginTransactionAsync(timeout.Token);
            await HoldFirstRowAsync(database, table.ObjectId, holder.Context.Sequence, timeout.Token);
            await using var upsert = await contender.BeginTransactionAsync(isolation, timeout.Token);
            using var waits = new LockWaitListener(database.Name);

            // Act: a completed row wait follows release of the proposal key.
            var pending = contender.ExecuteAsync("INSERT INTO t VALUES (1, 20) ON CONFLICT(id) DO UPDATE SET v = EXCLUDED.v RETURNING id, v",
                cancellationToken: timeout.Token).AsTask();
            await waits.WaitForWaitAsync(upsert.Context.Sequence, timeout.Token);
            (await writer.ExecuteAsync(delete ? "DELETE FROM t WHERE id = 1" : "UPDATE t SET id = 2 WHERE id = 1",
                cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);
            if (rollback) { await holder.RollbackAsync(timeout.Token); }
            else { await holder.CommitAsync(timeout.Token); }

            // Assert: rollback restores the original row; a committed change invalidates Snapshot.
            if (isolation == IsolationLevel.Snapshot && !rollback)
            {
                await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
                await upsert.RollbackAsync(timeout.Token);
            }
            else
            {
                await using var result = (await pending).ShouldBeAssignableTo<DatabaseResultSet>();
                result.AffectedCount.ShouldBe(1);
                (await RowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 20 });
                await upsert.CommitAsync(timeout.Token);
            }
            var rows = await QueryAsync(writer, "SELECT id, v FROM t ORDER BY id");
            var expected = new List<object?[]>();
            if (rollback || isolation == IsolationLevel.ReadCommitted) { expected.Add([1, 20]); }
            if (!delete && !rollback) { expected.Add([2, 10]); }
            rows.Count.ShouldBe(expected.Count);
            for (int i = 0; i < rows.Count; i++) { rows[i].ShouldBe(expected[i]); }
        });

    [Theory(DisplayName = "Cohesion Test [Sql] - Upsert arbitration: multi-row restart replays defaults and returns each effect once")]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExecuteAsync_MultiRowReturning_ShouldDiscardEveryPendingProposal(bool sideEffects)
        => RepeatAsync(async () =>
        {
            // Arrange: the first proposal is pending before the second reaches a held row.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            int calls = 0;
            var builder = SqlDatabaseEngine.CreateBuilder("upsert-restart-defaults");
            builder.Functions.Add(SqlScalarFunction.Create("next_value", (int _) => Interlocked.Increment(ref calls)));
            await using var engine = builder.Build();
            var database = await engine.CreateDatabaseAsync("app", timeout.Token);
            await using var writer = await database.CreateSessionAsync(timeout.Token);
            await using var contender = await database.CreateSessionAsync(timeout.Token);
            await writer.ExecuteAsync("CREATE TABLE t (id INT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, k INT UNIQUE, v INT" +
                (sideEffects ? " DEFAULT next_value(0)" : "") + ")", cancellationToken: timeout.Token);
            await writer.ExecuteAsync("INSERT INTO t(k, v) VALUES (2, 10)", cancellationToken: timeout.Token);
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            await using var holder = await writer.BeginTransactionAsync(timeout.Token);
            await HoldFirstRowAsync(database, table.ObjectId, holder.Context.Sequence, timeout.Token);
            await using var upsert = await contender.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
            using var waits = new LockWaitListener(database.Name);
            string sql = sideEffects
                ? "INSERT INTO t(k) VALUES (1), (2) ON CONFLICT(k) DO UPDATE SET v = EXCLUDED.v RETURNING id, k, v"
                : "INSERT INTO t(id, k, v) VALUES (2, 1, 20), (3, 2, 30) ON CONFLICT(k) DO UPDATE SET v = EXCLUDED.v RETURNING id, k, v";

            // Act: writing k=1 requires a key belonging to the earlier pending proposal.
            var pending = contender.ExecuteAsync(sql, cancellationToken: timeout.Token).AsTask();
            await waits.WaitForWaitAsync(upsert.Context.Sequence, timeout.Token);
            calls.ShouldBe(sideEffects ? 2 : 0);
            (await writer.ExecuteAsync("UPDATE t SET k = 3 WHERE k = 2", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);
            await holder.CommitAsync(timeout.Token);
            await using var result = (await pending).ShouldBeAssignableTo<DatabaseResultSet>();
            var returned = await RowsAsync(result);
            await upsert.CommitAsync(timeout.Token);

            // Assert: no pending insert or RETURNING result survived the restart.
            result.AffectedCount.ShouldBe(2);
            returned.Count.ShouldBe(2);
            returned.Select(row => row[1]).ShouldBe(new object?[] { 1, 2 });
            returned.Select(row => row[0]).ShouldBe(sideEffects ? new object?[] { 4, 5 } : new object?[] { 2, 3 });
            if (sideEffects)
            {
                calls.ShouldBe(4);
                returned.Select(row => row[2]).ShouldBe(new object?[] { 3, 4 });
            }
            (await QueryAsync(writer, "SELECT k FROM t ORDER BY k")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2, 3 });
        });

    [Theory(DisplayName = "Cohesion Test [Sql] - Upsert arbitration: back-off and cancellation preserve pre-statement grants")]
    [InlineData(false)]
    [InlineData(true)]
    public Task ExecuteAsync_PreStatementKey_ShouldSurviveBackoff(bool cancel)
        => RepeatAsync(async () =>
        {
            // Arrange: pre-statement data and its key must remain protected while arbitration waits.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            await using var engine = SqlDatabaseEngine.Create("upsert-preserved", new());
            var database = await engine.CreateDatabaseAsync("app", timeout.Token);
            await using var writer = await database.CreateSessionAsync(timeout.Token);
            await using var contender = await database.CreateSessionAsync(timeout.Token);
            await writer.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
            await writer.ExecuteAsync("INSERT INTO t VALUES (1, 10)", cancellationToken: timeout.Token);
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            var locks = database.Coordinator.LockManager;
            await using var holder = await writer.BeginTransactionAsync(timeout.Token);
            var row = await HoldFirstRowAsync(database, table.ObjectId, holder.Context.Sequence, timeout.Token);
            await using var upsert = await contender.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
            await contender.ExecuteAsync("INSERT INTO t VALUES (9, 90)", cancellationToken: timeout.Token);
            var retained = LockResource.Entry(table.ObjectId, Key(9).Hash());
            var probe = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
            using var waits = new LockWaitListener(database.Name);
            var pending = contender.ExecuteAsync("INSERT INTO t VALUES (9, 99), (1, 20) ON CONFLICT(id) DO UPDATE SET v = EXCLUDED.v",
                cancellationToken: cancellation.Token).AsTask();
            try
            {
                // Act / Assert: the old key remains held; both proposals' new grants were released.
                await waits.WaitForWaitAsync(upsert.Context.Sequence, timeout.Token);
                locks.TryAcquire(probe.Sequence, retained, LockMode.Exclusive).ShouldBeFalse();
                locks.TryAcquire(probe.Sequence, LockResource.Entry(table.ObjectId, Key(1).Hash()), LockMode.Exclusive).ShouldBeTrue();
                await database.Coordinator.RollbackAsync(probe, timeout.Token);
                if (cancel)
                {
                    cancellation.Cancel();
                    await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
                    locks.TryAcquire(holder.Context.Sequence, retained, LockMode.Exclusive).ShouldBeFalse();
                    await holder.RollbackAsync(timeout.Token);
                    var after = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
                    locks.TryAcquire(after.Sequence, row, LockMode.Exclusive).ShouldBeTrue();
                    locks.TryAcquire(after.Sequence, retained, LockMode.Exclusive).ShouldBeFalse();
                    await database.Coordinator.RollbackAsync(after, timeout.Token);
                    // Release the transaction only after proving cancellation preserved its earlier write.
                    (await QueryAsync(contender, "SELECT v FROM t WHERE id = 9")).ShouldHaveSingleItem()[0].ShouldBe(90);
                    await upsert.RollbackAsync(timeout.Token);
                    (await QueryAsync(writer, "SELECT id FROM t")).ShouldHaveSingleItem()[0].ShouldBe(1);
                }
                else
                {
                    await holder.RollbackAsync(timeout.Token);
                    (await pending).AffectedCount.ShouldBe(2);
                    await upsert.CommitAsync(timeout.Token);
                    (await QueryAsync(writer, "SELECT v FROM t ORDER BY id")).Select(values => values[0]).ShouldBe(new object?[] { 20, 99 });
                }
            }
            finally
            {
                if (probe.State == TransactionState.Active) { await database.Coordinator.RollbackAsync(probe, CancellationToken.None); }
            }
        });

    [Fact(DisplayName = "Cohesion Test [Sql] - Upsert arbitration: a foreign-key parent wait releases proposal keys before restarting")]
    public Task ExecuteAsync_ParentRowWait_ShouldReleaseProposalKeys()
        => RepeatAsync(async () =>
        {
            // Arrange: the proposed child's key is also the parent's replacement key.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var engine = SqlDatabaseEngine.Create("upsert-parent-retry", new());
            var database = await engine.CreateDatabaseAsync("app", timeout.Token);
            await using var writer = await database.CreateSessionAsync(timeout.Token);
            await using var contender = await database.CreateSessionAsync(timeout.Token);
            await writer.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, pid INT REFERENCES t(id))", cancellationToken: timeout.Token);
            await writer.ExecuteAsync("INSERT INTO t VALUES (1, NULL)", cancellationToken: timeout.Token);
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            await using var holder = await writer.BeginTransactionAsync(timeout.Token);
            await HoldFirstRowAsync(database, table.ObjectId, holder.Context.Sequence, timeout.Token);
            await using var upsert = await contender.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
            using var waits = new LockWaitListener(database.Name);

            // Act: the parent can take key 3 while the upsert waits for its shared row grant.
            var pending = contender.ExecuteAsync("INSERT INTO t VALUES (3, 1) ON CONFLICT DO NOTHING RETURNING id",
                cancellationToken: timeout.Token).AsTask();
            await waits.WaitForWaitAsync(upsert.Context.Sequence, timeout.Token);
            (await writer.ExecuteAsync("UPDATE t SET id = 3 WHERE id = 1", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);
            await holder.CommitAsync(timeout.Token);
            await using var result = (await pending).ShouldBeAssignableTo<DatabaseResultSet>();

            // Assert: re-arbitration finds the new conflict and discards the pending child.
            result.AffectedCount.ShouldBe(0);
            (await RowsAsync(result)).ShouldBeEmpty();
            await upsert.CommitAsync(timeout.Token);
            (await QueryAsync(writer, "SELECT id, pid FROM t")).ShouldHaveSingleItem().ShouldBe(new object?[] { 3, null });
        });

    [Fact(DisplayName = "Cohesion Test [Sql] - INSERT: parent-row wait precedes unique keys during a self-referencing parent rekey")]
    public Task ExecuteAsync_PlainInsertParentRekey_ShouldAvoidRowKeyCycle()
        => RepeatAsync(async () =>
        {
            // Arrange: if INSERT held key 3 while waiting for row 1, UPDATE would close a cycle.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var engine = SqlDatabaseEngine.Create("insert-parent-audit", new());
            var database = await engine.CreateDatabaseAsync("app", timeout.Token);
            await using var writer = await database.CreateSessionAsync(timeout.Token);
            await using var inserter = await database.CreateSessionAsync(timeout.Token);
            await writer.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, pid INT REFERENCES t(id))", cancellationToken: timeout.Token);
            await writer.ExecuteAsync("INSERT INTO t VALUES (1, NULL)", cancellationToken: timeout.Token);
            database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
            await using var holder = await writer.BeginTransactionAsync(timeout.Token);
            await HoldFirstRowAsync(database, table.ObjectId, holder.Context.Sequence, timeout.Token);
            await using var insertion = await inserter.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
            using var waits = new LockWaitListener(database.Name);

            // Act
            var pending = inserter.ExecuteAsync("INSERT INTO t VALUES (3, 1)", cancellationToken: timeout.Token).AsTask();
            await waits.WaitForWaitAsync(insertion.Context.Sequence, timeout.Token);
            (await writer.ExecuteAsync("UPDATE t SET id = 3 WHERE id = 1", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);
            await holder.CommitAsync(timeout.Token);

            // Assert: the parent disappeared; the refusal is a serialization abort, with no victim.
            await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
            await insertion.RollbackAsync(timeout.Token);
            (await QueryAsync(writer, "SELECT id, pid FROM t")).ShouldHaveSingleItem().ShouldBe(new object?[] { 3, null });
        });

    private static async Task<LockResource> HoldFirstRowAsync(SqlDatabase database, ulong objectId, TransactionSequence owner, CancellationToken cancellationToken)
    {
        using var iterator = database.DataStorage.GetUnitIterator(objectId);
        iterator.MoveNext().ShouldBeTrue();
        var unit = iterator.Current;
        var row = LockResource.Entry(objectId, SqlRecordLocation.Pack(unit.PageId, unit.SlotIndex));
        await database.Coordinator.LockManager.AcquireAsync(owner, row, LockMode.Exclusive, cancellationToken);
        return row;
    }

    private static IndexKey Key(int value)
    {
        var writer = new DatabaseKeyWriter();
        SqlRowCodec.AppendKeyValue(writer, DatabaseType.Int32, value, Collation.Binary);
        return IndexKey.From(writer);
    }

    private static IndexKey Key(string value)
    {
        var writer = new DatabaseKeyWriter();
        SqlRowCodec.AppendKeyValue(writer, DatabaseType.String, value, Collation.Binary);
        return IndexKey.From(writer);
    }

    private static async Task<List<object?[]>> QueryAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        return await RowsAsync(result);
    }

    private static async Task<List<object?[]>> RowsAsync(DatabaseResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }

    private static async Task RepeatAsync(Func<Task> run)
    {
        var elapsed = Stopwatch.StartNew();
        int exercised = 0;
        do
        {
            await run();
            exercised++;
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(45));
        }
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || exercised < 3);
        exercised.ShouldBeGreaterThanOrEqualTo(3);
    }
}
