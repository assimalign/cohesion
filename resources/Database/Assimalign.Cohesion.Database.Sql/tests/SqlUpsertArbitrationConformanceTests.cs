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
