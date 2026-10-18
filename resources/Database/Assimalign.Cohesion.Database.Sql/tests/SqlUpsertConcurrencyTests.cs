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
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlUpsertConcurrencyCollection
{
    public const string Name = "SQL upsert concurrency";
}

[Collection(SqlUpsertConcurrencyCollection.Name)]
public sealed class SqlUpsertConcurrencyTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Upsert: ReadCommitted validates a newer row against committed parents without seeing in-flight parents")]
    public async Task ExecuteAsync_ReadCommittedNewParent_ShouldUseRefreshedCommittedVisibility()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-fk-snapshot", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await first.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, pid INT REFERENCES p(id), v INT)", cancellationToken: timeout.Token);
        await first.ExecuteAsync("INSERT INTO p VALUES (1)", cancellationToken: timeout.Token);
        await first.ExecuteAsync("INSERT INTO t VALUES (1, 1, 10)", cancellationToken: timeout.Token);
        await using var winner = await first.BeginTransactionAsync(timeout.Token);
        await first.ExecuteAsync("INSERT INTO p VALUES (2)", cancellationToken: timeout.Token);
        await first.ExecuteAsync("UPDATE t SET pid = 2, v = 40 WHERE id = 1", cancellationToken: timeout.Token);
        await using var loser = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);

        // Act: both the updated child and its parent become visible while the upsert waits.
        var pending = second.ExecuteAsync("INSERT INTO t VALUES (1, 1, 1) ON CONFLICT (id) DO UPDATE SET v = t.v + EXCLUDED.v", cancellationToken: timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await winner.CommitAsync(timeout.Token);
        (await pending).AffectedCount.ShouldBe(1);
        await loser.CommitAsync(timeout.Token);

        // Assert: refreshing must still exclude a different transaction's in-flight parent.
        (await RowsAsync(second, "SELECT pid, v FROM t")).ShouldHaveSingleItem().ShouldBe(new object?[] { 2, 41 });
        await using var inFlight = await first.BeginTransactionAsync(timeout.Token);
        await first.ExecuteAsync("INSERT INTO p VALUES (3)", cancellationToken: timeout.Token);
        await using var writer = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await Should.ThrowAsync<SqlConstraintViolationException>(async () => await second.ExecuteAsync(
            "INSERT INTO t VALUES (2, 3, 1) ON CONFLICT (id) DO UPDATE SET v = EXCLUDED.v", cancellationToken: timeout.Token));
        await writer.RollbackAsync(timeout.Token);
        await inFlight.RollbackAsync(timeout.Token);
        (await RowsAsync(second, "SELECT id FROM t")).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Upsert: two gated Snapshot autocommit sessions produce exactly one insert and one retry")]
    public async Task ExecuteAsync_TwoAutocommitSessions_ShouldAbortOneNewKeyContender()
    {
        // Arrange: a kernel key lock admits neither proposal until both statement snapshots exist.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-auto", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        var writer = new DatabaseKeyWriter();
        SqlRowCodec.AppendKeyValue(writer, DatabaseType.Int32, 1, Collation.Binary);
        var gate = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        await database.Coordinator.LockManager.AcquireAsync(gate.Sequence,
            LockResource.Entry(table.ObjectId, IndexKey.From(writer).Hash()), LockMode.Exclusive, timeout.Token);

        // Act
        const string sql = "INSERT INTO t VALUES (1) ON CONFLICT DO NOTHING";
        var one = first.ExecuteAsync(sql, cancellationToken: timeout.Token).AsTask();
        var two = second.ExecuteAsync(sql, cancellationToken: timeout.Token).AsTask();
        one.IsCompleted.ShouldBeFalse();
        two.IsCompleted.ShouldBeFalse();
        await database.Coordinator.RollbackAsync(gate, timeout.Token);
        var outcomes = await Task.WhenAll(OutcomeAsync(one), OutcomeAsync(two));

        // Assert
        outcomes.Order().ShouldBe(new[] { 0, 1 });
        (await RowsAsync(first, "SELECT id FROM t")).ShouldHaveSingleItem()[0].ShouldBe(1);

        static async Task<int> OutcomeAsync(Task<DatabaseResult> task)
        {
            try { (await task).AffectedCount.ShouldBe(1); return 1; }
            catch (DatabaseTransactionAbortedException) { return 0; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Upsert: newer committed update versions follow Snapshot retry or ReadCommitted latest-row update")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task ExecuteAsync_NewerUpdatedVersion_ShouldFollowIsolation(IsolationLevel isolation)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-newversion", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
        await first.ExecuteAsync("INSERT INTO t VALUES (1, 10)", cancellationToken: timeout.Token);
        await using var winner = await first.BeginTransactionAsync(timeout.Token);
        await first.ExecuteAsync("UPDATE t SET v = 40 WHERE id = 1", cancellationToken: timeout.Token);
        await using var loser = await second.BeginTransactionAsync(isolation, timeout.Token);

        // Act
        var pending = second.ExecuteAsync("INSERT INTO t VALUES (1, 1) ON CONFLICT (id) DO UPDATE SET v = t.v + EXCLUDED.v", cancellationToken: timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await winner.CommitAsync(timeout.Token);

        // Assert
        if (isolation == IsolationLevel.Snapshot)
        {
            await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
            await loser.RollbackAsync(timeout.Token);
        }
        else
        {
            (await pending).AffectedCount.ShouldBe(1);
            await loser.CommitAsync(timeout.Token);
        }
        (await RowsAsync(first, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(isolation == IsolationLevel.Snapshot ? 40 : 41);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Upsert: key waits observe winner commit or rollback by isolation level")]
    [InlineData(IsolationLevel.Snapshot, false, false)]
    [InlineData(IsolationLevel.Snapshot, false, true)]
    [InlineData(IsolationLevel.Snapshot, true, false)]
    [InlineData(IsolationLevel.Snapshot, true, true)]
    [InlineData(IsolationLevel.ReadCommitted, false, false)]
    [InlineData(IsolationLevel.ReadCommitted, false, true)]
    [InlineData(IsolationLevel.ReadCommitted, true, false)]
    [InlineData(IsolationLevel.ReadCommitted, true, true)]
    public async Task ExecuteAsync_ContendedNewKey_ShouldFollowWinnerAndIsolation(IsolationLevel isolation, bool rollback, bool update)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-race", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
        await using var winner = await first.BeginTransactionAsync(isolation, timeout.Token);
        await using var loser = await second.BeginTransactionAsync(isolation, timeout.Token);
        string sql = "INSERT INTO t VALUES (1, 1) ON CONFLICT (id) " +
            (update ? "DO UPDATE SET v = t.v + EXCLUDED.v" : "DO NOTHING");
        (await first.ExecuteAsync(sql, cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);

        // Act: the second proposal cannot inspect the uncommitted index entry until its key lock releases.
        var pending = second.ExecuteAsync(sql, cancellationToken: timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        if (rollback) { await winner.RollbackAsync(timeout.Token); }
        else { await winner.CommitAsync(timeout.Token); }

        // Assert
        if (!rollback && isolation == IsolationLevel.Snapshot)
        {
            await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
            await loser.RollbackAsync(timeout.Token);
        }
        else
        {
            (await pending).AffectedCount.ShouldBe(rollback || update ? 1 : 0);
            await loser.CommitAsync(timeout.Token);
        }
        var rows = await RowsAsync(first, "SELECT id, v FROM t");
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1, !rollback && update && isolation == IsolationLevel.ReadCommitted ? 2 : 1 });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Upsert: racing Snapshot autocommit DO NOTHING aborts one contender")]
    public async Task ExecuteAsync_AutocommitSnapshotWait_ShouldAbortNewerConflict()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-race", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        await using var winner = await first.BeginTransactionAsync(timeout.Token);
        await first.ExecuteAsync("INSERT INTO t VALUES (1) ON CONFLICT DO NOTHING", cancellationToken: timeout.Token);

        // Act
        var pending = second.ExecuteAsync("INSERT INTO t VALUES (1) ON CONFLICT DO NOTHING", cancellationToken: timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await winner.CommitAsync(timeout.Token);

        // Assert
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
        (await second.ExecuteAsync("INSERT INTO t VALUES (1) ON CONFLICT DO NOTHING", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(0);
        (await RowsAsync(first, "SELECT id FROM t")).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Upsert: rollback restores inserted and updated rows and unique entries")]
    public async Task ExecuteAsync_TransactionRollback_ShouldRestoreAllIndexes()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("upsert-rollback", new());
        var database = await engine.CreateDatabaseAsync("app");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT UNIQUE)");
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 10)");
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);

        // Act
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 20), (2, 30) ON CONFLICT (id) DO UPDATE SET v = EXCLUDED.v");
        await transaction.RollbackAsync(CancellationToken.None);

        // Assert
        (await RowsAsync(session, "SELECT id, v FROM t WHERE v = 10")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 10 });
        (await RowsAsync(session, "SELECT id FROM t WHERE id = 2")).ShouldBeEmpty();
        (await session.ExecuteAsync("INSERT INTO t VALUES (1, 40) ON CONFLICT DO NOTHING")).AffectedCount.ShouldBe(0);
        (await session.ExecuteAsync("INSERT INTO t VALUES (2, 20), (3, 30)")).AffectedCount.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Upsert: referential row/key cycles are detected and the surviving upsert completes")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public async Task ExecuteAsync_ReferentialActionRowKeyCycle_ShouldReportDeadlock(string action)
    {
        // Arrange: hold the exact child row a referential action will update, before its key locks.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-deadlock", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var deleter = await database.CreateSessionAsync(timeout.Token);
        await using var upserter = await database.CreateSessionAsync(timeout.Token);
        await deleter.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        await deleter.ExecuteAsync($"CREATE TABLE c (id INT PRIMARY KEY, pid INT DEFAULT 2 REFERENCES p(id) ON DELETE {action}, v INT)", cancellationToken: timeout.Token);
        await deleter.ExecuteAsync("INSERT INTO p VALUES (1), (2)", cancellationToken: timeout.Token);
        await deleter.ExecuteAsync("INSERT INTO c VALUES (10, 1, 0)", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "c", out var child).ShouldBeTrue();
        using var iterator = database.DataStorage.GetUnitIterator(child.ObjectId);
        iterator.MoveNext().ShouldBeTrue();
        var unit = iterator.Current;
        ulong location = SqlRecordLocation.Pack(unit.PageId, unit.SlotIndex);
        await using var deletion = await deleter.BeginTransactionAsync(timeout.Token);
        await database.Coordinator.LockManager.AcquireAsync(deletion.Context.Sequence,
            LockResource.Entry(child.ObjectId, location), LockMode.Exclusive, timeout.Token);
        await using var upsert = await upserter.BeginTransactionAsync(timeout.Token);

        // Act: upsert owns the arbiter key and waits for the child row. The SET action needs that key.
        var pending = upserter.ExecuteAsync("INSERT INTO c VALUES (10, 1, 1) ON CONFLICT (id) DO UPDATE SET v = EXCLUDED.v", cancellationToken: timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await Should.ThrowAsync<DatabaseTransactionDeadlockException>(async () =>
            await deleter.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: timeout.Token));
        await deletion.RollbackAsync(timeout.Token);
        (await pending).AffectedCount.ShouldBe(1);
        await upsert.CommitAsync(timeout.Token);

        // Assert
        (await RowsAsync(deleter, "SELECT pid, v FROM c")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 1 });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Upsert: sorted multi-row locks interoperate with a concurrent plain INSERT")]
    public async Task ExecuteAsync_PlainInsertAndReversedKeys_ShouldWaitWithoutDeadlock()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-insert", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        await using var plain = await first.BeginTransactionAsync(timeout.Token);
        await first.ExecuteAsync("INSERT INTO t VALUES (1), (2)", cancellationToken: timeout.Token);
        await using var upsert = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);

        // Act
        var pending = second.ExecuteAsync("INSERT INTO t VALUES (2), (1), (3) ON CONFLICT DO NOTHING", cancellationToken: timeout.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        await plain.CommitAsync(timeout.Token);
        (await pending).AffectedCount.ShouldBe(1);
        await upsert.CommitAsync(timeout.Token);

        // Assert
        (await RowsAsync(first, "SELECT id FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2, 3 });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Upsert: retried concurrent increments lose no committed updates")]
    public async Task ExecuteAsync_ConcurrentIncrements_ShouldReachExactCommittedTotal()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("upsert-increments", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var setup = await database.CreateSessionAsync(timeout.Token);
        await setup.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
        await setup.ExecuteAsync("INSERT INTO t VALUES (1, 0)", cancellationToken: timeout.Token);
        int committed = 0;
        int aborted = 0;
        int attempts = 0;
        var clock = Stopwatch.StartNew();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act: require both a minimum duration and observed commits/serialization retries, bounded by the token.
        var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            await using var session = await database.CreateSessionAsync(timeout.Token);
            await start.Task;
            do
            {
                timeout.Token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref attempts);
                try
                {
                    await session.ExecuteAsync("INSERT INTO t VALUES (1, 1) ON CONFLICT (id) DO UPDATE SET v = t.v + EXCLUDED.v", cancellationToken: timeout.Token);
                    Interlocked.Increment(ref committed);
                }
                catch (DatabaseTransactionAbortedException) { Interlocked.Increment(ref aborted); }
            } while (clock.Elapsed < TimeSpan.FromSeconds(1) || Volatile.Read(ref committed) < 30 || Volatile.Read(ref aborted) == 0);
        }, timeout.Token)).ToArray();
        start.SetResult();
        await Task.WhenAll(tasks);

        // Assert
        committed.ShouldBeGreaterThanOrEqualTo(30);
        aborted.ShouldBeGreaterThan(0);
        attempts.ShouldBe(committed + aborted);
        (await RowsAsync(setup, "SELECT v FROM t WHERE id = 1")).ShouldHaveSingleItem()[0].ShouldBe(committed);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
