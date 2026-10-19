using System;
using System.Collections.Generic;
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

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlUpsertConcurrencyCollection
{
    public const string Name = "SQL upsert concurrency";
}

[Collection(SqlUpsertConcurrencyCollection.Name)]
public sealed class SqlUpsertConcurrencyTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Upsert: unique-index DDL while waiting invalidates the bound arbiter set")]
    [InlineData("CREATE UNIQUE INDEX by_v ON t(v)", "INSERT INTO t VALUES (2, 1) ON CONFLICT DO NOTHING", false, 0)]
    [InlineData("DROP INDEX uq ON t", "INSERT INTO t VALUES (1, 2) ON CONFLICT (id) DO UPDATE SET v = EXCLUDED.v", true, 0)]
    [InlineData("DROP INDEX uq ON t|CREATE UNIQUE INDEX uq ON t(id)", "INSERT INTO t VALUES (1, 2) ON CONFLICT ON CONSTRAINT uq DO UPDATE SET v = EXCLUDED.v", false, 1)]
    public async Task ExecuteAsync_ArbiterDdlWhileWaiting_ShouldAbortAndRequireFreshBinding(string ddl, string sql, bool retryFails, int retriedCount)
    {
        // Arrange: block the write after planning, as SqlConstraintRaceTests gates index DDL.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-arbiter-ddl", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t (id INT, v INT)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE UNIQUE INDEX uq ON t(id)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 1)", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        var holder = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        try
        {
            await database.Coordinator.LockManager.AcquireAsync(holder.Sequence,
                LockResource.Object(table.ObjectId), LockMode.Exclusive, timeout.Token);
            var pending = session.ExecuteAsync(sql, cancellationToken: timeout.Token).AsTask();
            pending.IsCompleted.ShouldBeFalse();

            // Act: publish real index changes before the statement obtains its object intent lock.
            var executor = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            foreach (string change in ddl.Split('|'))
            {
                await executor.ExecuteAsync(SqlQueryRequest.FromSql(change),
                    new SqlStatementContext(holder, database.Coordinator), timeout.Token);
            }
            await database.Coordinator.CommitAsync(holder, timeout.Token);

            // Assert: no discarded arbiter can silently turn the statement into a plain insert.
            var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
            failure.Message.ShouldContain("unique index definitions", Case.Sensitive);
            (await RowsAsync(session, "SELECT id, v FROM t")).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 1 });
            if (retryFails)
            {
                var invalid = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: timeout.Token));
                invalid.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE035");
            }
            else { (await session.ExecuteAsync(sql, cancellationToken: timeout.Token)).AffectedCount.ShouldBe(retriedCount); }
        }
        finally
        {
            if (holder.State == TransactionState.Active) { await database.Coordinator.RollbackAsync(holder, CancellationToken.None); }
        }
    }

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
        using var waits = new LockWaitListener(database.Name);
        var pending = second.ExecuteAsync("INSERT INTO t VALUES (1, 1, 1) ON CONFLICT (id) DO UPDATE SET v = t.v + EXCLUDED.v", cancellationToken: timeout.Token).AsTask();
        await waits.WaitForWaitAsync(loser.Context.Sequence, timeout.Token);
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
        using var waits = new LockWaitListener(database.Name);
        var pending = second.ExecuteAsync("INSERT INTO t VALUES (1, 1) ON CONFLICT (id) DO UPDATE SET v = t.v + EXCLUDED.v", cancellationToken: timeout.Token).AsTask();
        await waits.WaitForWaitAsync(loser.Context.Sequence, timeout.Token);
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
        using var waits = new LockWaitListener(database.Name);
        var pending = second.ExecuteAsync(sql, cancellationToken: timeout.Token).AsTask();
        await waits.WaitForWaitAsync(loser.Context.Sequence, timeout.Token);
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
        using var waits = new LockWaitListener(database.Name);
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

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Upsert: referential row/key waits release arbitration keys and complete without a victim")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public async Task ExecuteAsync_ReferentialActionRowKeyCycle_ShouldCompleteWithoutVictim(string action)
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
        await using var upsert = await upserter.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);

        // Act: upsert releases the arbiter key before waiting for the child row. The SET action can take it.
        using var waits = new LockWaitListener(database.Name);
        var pending = upserter.ExecuteAsync("INSERT INTO c VALUES (10, 1, 1) ON CONFLICT (id) DO UPDATE SET v = EXCLUDED.v", cancellationToken: timeout.Token).AsTask();
        await waits.WaitForWaitAsync(upsert.Context.Sequence, timeout.Token);
        (await deleter.ExecuteAsync("DELETE FROM p WHERE id = 1", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);
        await deletion.CommitAsync(timeout.Token);
        (await pending).AffectedCount.ShouldBe(1);
        await upsert.CommitAsync(timeout.Token);

        // Assert
        (await RowsAsync(deleter, "SELECT pid, v FROM c")).ShouldHaveSingleItem().ShouldBe(new object?[] { action == "SET NULL" ? null : 2, 1 });
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
        using var waits = new LockWaitListener(database.Name);
        var pending = second.ExecuteAsync("INSERT INTO t VALUES (2), (1), (3) ON CONFLICT DO NOTHING", cancellationToken: timeout.Token).AsTask();
        await waits.WaitForWaitAsync(upsert.Context.Sequence, timeout.Token);
        await plain.CommitAsync(timeout.Token);
        (await pending).AffectedCount.ShouldBe(1);
        await upsert.CommitAsync(timeout.Token);

        // Assert
        (await RowsAsync(first, "SELECT id FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2, 3 });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Upsert: gated increments retry Snapshot losers without losing updates")]
    public async Task ExecuteAsync_ConcurrentIncrements_ShouldReachExactCommittedTotal()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-increments", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
        await first.ExecuteAsync("INSERT INTO t VALUES (1, 0)", cancellationToken: timeout.Token);
        const string sql = "INSERT INTO t VALUES (1, 1) ON CONFLICT (id) DO UPDATE SET v = t.v + EXCLUDED.v";
        int committed = 0;
        int aborted = 0;

        // Act: each round proves the second transaction reached its key wait before commit.
        for (int iteration = 0; iteration < 3; iteration++)
        {
            await using var winner = await first.BeginTransactionAsync(timeout.Token);
            await using var loser = await second.BeginTransactionAsync(timeout.Token);
            await first.ExecuteAsync(sql, cancellationToken: timeout.Token);
            using var waits = new LockWaitListener(database.Name);
            var pending = second.ExecuteAsync(sql, cancellationToken: timeout.Token).AsTask();
            await waits.WaitForWaitAsync(loser.Context.Sequence, timeout.Token);
            await winner.CommitAsync(timeout.Token);
            committed++;
            await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
            aborted++;
            await loser.RollbackAsync(timeout.Token);
            await second.ExecuteAsync(sql, cancellationToken: timeout.Token);
            committed++;
        }

        // Assert
        committed.ShouldBe(6);
        aborted.ShouldBe(3);
        (await RowsAsync(first, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(committed);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Upsert: all proposal unique keys precede a racing plain insert")]
    public async Task ExecuteAsync_SecondUniqueKeyBeforeArbiterHash_ShouldWaitWithoutCycle()
    {
        // Arrange: the old arbiter-only batch acquired the larger hash first and deadlocked.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-all-keys", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, email TEXT UNIQUE, v INT)", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        var emailWriter = new DatabaseKeyWriter();
        SqlRowCodec.AppendKeyValue(emailWriter, DatabaseType.String, "mail", Collation.Binary);
        var emailKey = IndexKey.From(emailWriter);
        int id = Enumerable.Range(1, 1000).First(value => IdKey(value).Hash() > emailKey.Hash());
        IdKey(id).Hash().ShouldBeGreaterThan(emailKey.Hash());
        await using var plain = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await database.Coordinator.LockManager.AcquireAsync(plain.Context.Sequence,
            LockResource.Entry(table.ObjectId, emailKey.Hash()), LockMode.Exclusive, timeout.Token);
        await using var upsert = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        using var waits = new LockWaitListener(database.Name);

        // Act
        var pending = second.ExecuteAsync($"INSERT INTO t VALUES ({id}, 'mail', 2) ON CONFLICT(id) DO UPDATE SET v = EXCLUDED.v", cancellationToken: timeout.Token).AsTask();
        await waits.WaitForWaitAsync(upsert.Context.Sequence, timeout.Token);
        await first.ExecuteAsync($"INSERT INTO t VALUES ({id}, 'mail', 1)", cancellationToken: timeout.Token);
        await plain.CommitAsync(timeout.Token);
        (await pending).AffectedCount.ShouldBe(1);
        await upsert.CommitAsync(timeout.Token);

        // Assert
        (await RowsAsync(first, "SELECT id, email, v FROM t")).ShouldHaveSingleItem().ShouldBe(new object?[] { id, "mail", 2 });

        static IndexKey IdKey(int value)
        {
            var writer = new DatabaseKeyWriter();
            SqlRowCodec.AppendKeyValue(writer, DatabaseType.Int32, value, Collation.Binary);
            return IndexKey.From(writer);
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Upsert: different unique targets on one row share the complete key batch")]
    public async Task ExecuteAsync_DifferentArbitersSameRow_ShouldSerialize()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-targets", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, email TEXT UNIQUE, v INT)", cancellationToken: timeout.Token);
        await first.ExecuteAsync("INSERT INTO t VALUES (1, 'mail', 0)", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        using var iterator = database.DataStorage.GetUnitIterator(table.ObjectId);
        iterator.MoveNext().ShouldBeTrue();
        var unit = iterator.Current;
        var gate = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await database.Coordinator.LockManager.AcquireAsync(gate.Sequence,
            LockResource.Entry(table.ObjectId, SqlRecordLocation.Pack(unit.PageId, unit.SlotIndex)), LockMode.Exclusive, timeout.Token);
        await using var one = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await using var two = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        using var waits = new LockWaitListener(database.Name);

        // Act: both wait on the held row after releasing their unique keys. Either restart can win.
        var pendingOne = first.ExecuteAsync("INSERT INTO t VALUES (1, 'mail', 1) ON CONFLICT(id) DO UPDATE SET v = t.v + EXCLUDED.v", cancellationToken: timeout.Token).AsTask();
        await waits.WaitForWaitAsync(one.Context.Sequence, timeout.Token);
        var pendingTwo = second.ExecuteAsync("INSERT INTO t VALUES (1, 'mail', 2) ON CONFLICT(email) DO UPDATE SET v = t.v + EXCLUDED.v", cancellationToken: timeout.Token).AsTask();
        await waits.WaitForWaitAsync(two.Context.Sequence, timeout.Token);
        await database.Coordinator.RollbackAsync(gate, timeout.Token);
        var completed = await Task.WhenAny(pendingOne, pendingTwo);
        (await completed).AffectedCount.ShouldBe(1);
        if (ReferenceEquals(completed, pendingOne))
        {
            await one.CommitAsync(timeout.Token);
            (await pendingTwo).AffectedCount.ShouldBe(1);
            await two.CommitAsync(timeout.Token);
        }
        else
        {
            await two.CommitAsync(timeout.Token);
            (await pendingOne).AffectedCount.ShouldBe(1);
            await one.CommitAsync(timeout.Token);
        }

        // Assert
        (await RowsAsync(first, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Upsert: Snapshot checks a newer conflict before a false filter")]
    public async Task ExecuteAsync_NewerConflictFalseFilter_ShouldAbort()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-filter", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await first.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        await using var winner = await first.BeginTransactionAsync(timeout.Token);
        await using var loser = await second.BeginTransactionAsync(timeout.Token);
        await first.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: timeout.Token);
        using var waits = new LockWaitListener(database.Name);

        // Act
        var pending = second.ExecuteAsync("INSERT INTO t VALUES (1) ON CONFLICT(id) DO UPDATE SET id = 2 WHERE false", cancellationToken: timeout.Token).AsTask();
        await waits.WaitForWaitAsync(loser.Context.Sequence, timeout.Token);
        await winner.CommitAsync(timeout.Token);

        // Assert
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
        await loser.RollbackAsync(timeout.Token);
        (await RowsAsync(first, "SELECT id FROM t")).ShouldHaveSingleItem()[0].ShouldBe(1);
    }
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Upsert: unchanged unique values repoint entries after arbitration releases its row wait keys")]
    public async Task ExecuteAsync_ValueOnlyUpdateRowKeyCycle_ShouldCompleteWithoutVictim()
    {
        // Arrange: the storage update creates a fresh heap/index version even for SET v = v + 1.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var engine = SqlDatabaseEngine.Create("upsert-update-cycle", new());
        var database = await engine.CreateDatabaseAsync("app", timeout.Token);
        await using var updater = await database.CreateSessionAsync(timeout.Token);
        await using var upserter = await database.CreateSessionAsync(timeout.Token);
        await updater.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
        await updater.ExecuteAsync("INSERT INTO t VALUES (1, 0)", cancellationToken: timeout.Token);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        using var iterator = database.DataStorage.GetUnitIterator(table.ObjectId);
        iterator.MoveNext().ShouldBeTrue();
        var unit = iterator.Current;
        await using var update = await updater.BeginTransactionAsync(timeout.Token);
        await database.Coordinator.LockManager.AcquireAsync(update.Context.Sequence,
            LockResource.Entry(table.ObjectId, SqlRecordLocation.Pack(unit.PageId, unit.SlotIndex)), LockMode.Exclusive, timeout.Token);
        await using var upsert = await upserter.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        using var waits = new LockWaitListener(database.Name);

        // Act: wait for evidence that the upsert released its key and queued on the row.
        var pending = upserter.ExecuteAsync("INSERT INTO t VALUES (1, 1) ON CONFLICT(id) DO UPDATE SET v = t.v + EXCLUDED.v", cancellationToken: timeout.Token).AsTask();
        await waits.WaitForWaitAsync(upsert.Context.Sequence, timeout.Token);
        (await updater.ExecuteAsync("UPDATE t SET v = v + 1 WHERE id = 1", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);
        await update.CommitAsync(timeout.Token);
        (await pending).AffectedCount.ShouldBe(1);
        await upsert.CommitAsync(timeout.Token);

        // Assert: both statements committed exactly once.
        (await RowsAsync(updater, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(2);
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
