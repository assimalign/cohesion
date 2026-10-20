using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlAlterTableRecoveryTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - RENAME: every crash boundary restores a complete FK directory")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_RenameCrashPoints_ShouldObserveOnlyCompleteDefinitions(bool column)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("rename-crash", new SqlDatabaseEngineOptions { StorageStrategy = storage });
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlAlterTableExecutionTests.SeedRenameAsync(session);
        using var points = storage.RecordCrashPoints();

        // Act
        await SqlAlterTableExecutionTests.ExecuteAsync(session, column ? "ALTER TABLE p RENAME id TO key_id" : "ALTER TABLE p RENAME TO renamed");
        points.Dispose();
        int oldCount = 0, newCount = 0;

        // Assert: each metadata/data write and each durability boundary is reopened, including before commit.
        for (int index = 0; index < points.Count; index++)
        {
            await using var reopened = SqlDatabaseEngine.Create("rename-crash-reopen", new SqlDatabaseEngineOptions { StorageStrategy = points.Open(index) });
            var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
            bool renamed = column ? restored.Catalog.Tables.Single(table => table.Name == "p").FindColumn("key_id") is not null
                : restored.Catalog.Tables.Any(table => table.Name == "renamed");
            if (renamed) { newCount++; } else { oldCount++; }
            string parent = !column && renamed ? "renamed" : "p";
            string key = column && renamed ? "key_id" : "id";
            restored.Catalog.TryGetTable("dbo", "c", out var child).ShouldBeTrue();
            child.Constraints.Single().ReferencedTable.ShouldBe(parent);
            child.Constraints.Single().ReferencedColumns.ShouldBe(new[] { key });
            restored.Catalog.TryGetTable("dbo", parent, out var table).ShouldBeTrue();
            var self = table.Constraints.Single(constraint => constraint.ReferencedTable is not null);
            self.ReferencedTable.ShouldBe(parent);
            self.ReferencedColumns.ShouldBe(new[] { key });
            (await SqlAlterTableExecutionTests.RowsAsync(reader, $"SELECT {key} FROM {parent} ORDER BY {key}")).Count.ShouldBe(2);
            await SqlAlterTableExecutionTests.ExecuteAsync(reader, $"DELETE FROM {parent} WHERE {key} = 1");
            (await SqlAlterTableExecutionTests.RowsAsync(reader, "SELECT * FROM c")).ShouldBeEmpty();
        }
        oldCount.ShouldBeGreaterThan(0);
        newCount.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ALTER lock waits: stale DML refuses instead of using old metadata")]
    [InlineData("RENAME TO renamed", "Table 'dbo.t' does not exist.")]
    [InlineData("RENAME v TO renamed", "changed while the statement was waiting")]
    [InlineData("ALTER v SET DEFAULT 9", "changed while the statement was waiting")]
    [InlineData("ALTER v DROP DEFAULT", "changed while the statement was waiting")]
    [InlineData("ALTER v SET NOT NULL", "changed while the statement was waiting")]
    [InlineData("ALTER v DROP NOT NULL", "changed while the statement was waiting")]
    public async Task ExecuteAsync_WriterWaitingBehindAlter_ShouldRefuseStaleDefinition(string action, string message)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("rename-wait", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await SqlAlterTableExecutionTests.ExecuteAsync(writer, "CREATE TABLE t(id INT, v INT NOT NULL DEFAULT 7)");
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        var coordinator = database.Coordinator;
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        try
        {
            await coordinator.LockManager.AcquireAsync(context.Sequence, LockResource.Object(table.ObjectId), LockMode.Exclusive, TestTimeout.Token());
            var waiting = writer.ExecuteAsync("INSERT INTO t(id) VALUES (1)", cancellationToken: TestTimeout.Token()).AsTask();
            waiting.IsCompleted.ShouldBeFalse();

            // Act: actual DDL uses the lock-owning context, the same path the session drives.
            var executor = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            await executor.ExecuteAsync(SqlQueryRequest.FromSql("ALTER TABLE t " + action), new SqlStatementContext(context, coordinator), TestTimeout.Token());
            await coordinator.CommitAsync(context, TestTimeout.Token());

            // Assert
            (await Should.ThrowAsync<DatabaseException>(async () => await waiting)).Message.ShouldContain(message, Case.Sensitive);
        }
        finally
        {
            if (context.State == TransactionState.Active) { await coordinator.RollbackAsync(context, CancellationToken.None); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RENAME concurrency: sessions rename parents sharing children without deadlocks")]
    public async Task ExecuteAsync_TwoSessionsRenamingRelatedTables_ShouldMakeProgressAndPreserveReferences()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("rename-sessions", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var one = await database.CreateSessionAsync(CancellationToken.None);
        await using var two = await database.CreateSessionAsync(CancellationToken.None);
        await SqlAlterTableExecutionTests.ExecuteAsync(one, "CREATE TABLE a(id INT PRIMARY KEY)");
        await SqlAlterTableExecutionTests.ExecuteAsync(one, "CREATE TABLE b(id INT PRIMARY KEY)");
        await SqlAlterTableExecutionTests.ExecuteAsync(one, "CREATE TABLE c1(aid INT REFERENCES a(id), bid INT REFERENCES b(id))");
        await SqlAlterTableExecutionTests.ExecuteAsync(one, "CREATE TABLE c2(bid INT REFERENCES b(id), aid INT REFERENCES a(id))");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> RenameAsync(SqlDatabaseSession session, string original, string alternate)
        {
            await start.Task;
            var clock = Stopwatch.StartNew();
            int completed = 0;
            string current = original;
            while ((clock.Elapsed < TimeSpan.FromSeconds(1) || completed < 16 || current != original) && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                string target = current == original ? alternate : original;
                try
                {
                    var result = await session.ExecuteAsync($"ALTER TABLE {current} RENAME TO {target}", cancellationToken: timeout.Token);
                    result.Status.ShouldBe(Assimalign.Cohesion.Database.Execution.DatabaseResultStatus.Success);
                    completed++;
                    current = target;
                }
                catch (DatabaseException error) when (error.Message.Contains("changed while", StringComparison.Ordinal) ||
                    error.Message.Contains("does not exist", StringComparison.Ordinal)) { }
            }
            current.ShouldBe(original);
            return completed;
        }

        // Act: run at least one second and until both counters prove repeated publication.
        var first = Task.Run(() => RenameAsync(one, "a", "aa"), timeout.Token);
        var second = Task.Run(() => RenameAsync(two, "b", "bb"), timeout.Token);
        start.SetResult();
        int[] completed = await Task.WhenAll(first, second);

        // Assert
        completed[0].ShouldBeGreaterThanOrEqualTo(16);
        completed[1].ShouldBeGreaterThanOrEqualTo(16);
        database.Catalog.TryGetTable("dbo", "a", out var a).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "b", out var b).ShouldBeTrue();
        foreach (var child in database.Catalog.Tables.Where(table => table.Name is "c1" or "c2"))
        {
            child.Constraints.Select(constraint => constraint.ReferencedTable).OrderBy(name => name).ShouldBe(new[] { "a", "b" });
        }
        await SqlAlterTableExecutionTests.ExecuteAsync(one, "INSERT INTO a VALUES (1)");
        await SqlAlterTableExecutionTests.ExecuteAsync(two, "INSERT INTO b VALUES (2)");
        await SqlAlterTableExecutionTests.ExecuteAsync(one, "INSERT INTO c1 VALUES (1, 2)");
        await SqlAlterTableExecutionTests.ExecuteAsync(two, "INSERT INTO c2 VALUES (2, 1)");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RENAME deadlock: related table DDL detects its cycle and remains usable")]
    public async Task ExecuteAsync_RelatedTableRenames_ShouldDetectCycleAndPermitRetry()
    {
        // Arrange: each statement has its target lock; mutual FKs make each need the other table.
        await using var engine = SqlDatabaseEngine.Create("rename-deadlock", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var firstSession = await database.CreateSessionAsync(CancellationToken.None);
        await using var secondSession = await database.CreateSessionAsync(CancellationToken.None);
        await SqlAlterTableExecutionTests.ExecuteAsync(firstSession, "CREATE TABLE a(id INT PRIMARY KEY, bid INT)");
        await SqlAlterTableExecutionTests.ExecuteAsync(firstSession, "CREATE TABLE b(id INT PRIMARY KEY, aid INT REFERENCES a(id))");
        await SqlAlterTableExecutionTests.ExecuteAsync(firstSession, "ALTER TABLE a ADD CONSTRAINT fk_b FOREIGN KEY (bid) REFERENCES b(id)");
        database.Catalog.TryGetTable("dbo", "a", out var a).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "b", out var b).ShouldBeTrue();
        var coordinator = database.Coordinator;
        var first = await coordinator.BeginAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        var second = await coordinator.BeginAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        try
        {
            await coordinator.LockManager.AcquireAsync(first.Sequence, LockResource.Object(a.ObjectId), LockMode.Exclusive, TestTimeout.Token());
            await coordinator.LockManager.AcquireAsync(second.Sequence, LockResource.Object(b.ObjectId), LockMode.Exclusive, TestTimeout.Token());
            var one = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var two = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var pending = one.ExecuteAsync(SqlQueryRequest.FromSql("ALTER TABLE a RENAME TO renamed_a"), new SqlStatementContext(first, coordinator), TestTimeout.Token());
            pending.IsCompleted.ShouldBeFalse();

            // Act / Assert: deterministic interleaving exercises one real cycle, with no timing window.
            await Should.ThrowAsync<TransactionDeadlockException>(() => two.ExecuteAsync(SqlQueryRequest.FromSql("ALTER TABLE b RENAME TO renamed_b"),
                new SqlStatementContext(second, coordinator), TestTimeout.Token()));
            await coordinator.RollbackAsync(second, CancellationToken.None);
            await pending;
            await coordinator.CommitAsync(first, TestTimeout.Token());
            await SqlAlterTableExecutionTests.ExecuteAsync(secondSession, "ALTER TABLE b RENAME TO renamed_b");
            await SqlAlterTableExecutionTests.ExecuteAsync(firstSession, "INSERT INTO renamed_a VALUES (1, NULL)");
            await SqlAlterTableExecutionTests.ExecuteAsync(secondSession, "INSERT INTO renamed_b VALUES (2, 1)");
            (await SqlAlterTableExecutionTests.RowsAsync(firstSession, "SELECT * FROM renamed_b")).Count.ShouldBe(1);
        }
        finally
        {
            if (first.State == TransactionState.Active) { await coordinator.RollbackAsync(first, CancellationToken.None); }
            if (second.State == TransactionState.Active) { await coordinator.RollbackAsync(second, CancellationToken.None); }
        }
    }
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - ALTER COLUMN: frozen missing literals validate with no live rows")]
    [InlineData("abcdefgh", false)]
    [InlineData("abc", true)]
    public async Task ExecuteAsync_NarrowedMissingValue_ShouldKeepRetainedVersionsReadable(string literal, bool fits)
    {
        // Arrange: the old transaction touches only another table, retaining t's tombstone.
        await using var engine = SqlDatabaseEngine.Create("alter-frozen-type", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await SqlAlterTableExecutionTests.ExecuteAsync(writer, "CREATE TABLE sentinel(id INT)");
        await SqlAlterTableExecutionTests.ExecuteAsync(writer, "CREATE TABLE t(id INT)");
        await SqlAlterTableExecutionTests.ExecuteAsync(writer, "INSERT INTO t VALUES (1)");
        await SqlAlterTableExecutionTests.ExecuteAsync(writer, $"ALTER TABLE t ADD c VARCHAR(10) DEFAULT '{literal}'");
        await using var retained = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await SqlAlterTableExecutionTests.RowsAsync(reader, "SELECT * FROM sentinel");
        await SqlAlterTableExecutionTests.ExecuteAsync(writer, "DELETE FROM t");
        await SqlAlterTableExecutionTests.ExecuteAsync(writer, "ALTER TABLE t ALTER c DROP DEFAULT");
        database.Catalog.TryGetTable("dbo", "t", out var before).ShouldBeTrue();
        ulong generation = database.Catalog.DdlGeneration;

        // Act
        if (fits) { await SqlAlterTableExecutionTests.ExecuteAsync(writer, "ALTER TABLE t ALTER COLUMN c VARCHAR(5)"); }
        else
        {
            var error = await Should.ThrowAsync<DatabaseException>(() => SqlAlterTableExecutionTests.ExecuteAsync(writer, "ALTER TABLE t ALTER COLUMN c VARCHAR(5)"));
            error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE012");
            error.Message.ShouldContain("Column 'c': frozen missing value", Case.Sensitive);
        }

        // Assert: current and retained scans decode every version, even the invisible short one.
        database.Catalog.TryGetTable("dbo", "t", out var after).ShouldBeTrue();
        if (!fits)
        {
            after.ShouldBeSameAs(before);
            database.Catalog.DdlGeneration.ShouldBe(generation);
        }
        after.FindColumn("c")!.MissingValueLiteral.ShouldBe($"'{literal}'");
        (await SqlAlterTableExecutionTests.RowsAsync(writer, "SELECT * FROM t")).ShouldBeEmpty();
        (await SqlAlterTableExecutionTests.RowsAsync(reader, "SELECT c FROM t")).ShouldHaveSingleItem()[0].ShouldBe(literal);
        await SqlAlterTableExecutionTests.ExecuteAsync(writer, "CREATE INDEX ix_c ON t(c)");
        await SqlAlterTableExecutionTests.ExecuteAsync(writer, "UPDATE t SET c = 'x'");
        await SqlAlterTableExecutionTests.ExecuteAsync(writer, "DELETE FROM t");
        await retained.RollbackAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - SELECT racing RENAME: table and index metadata use one catalog generation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SelectRacingColumnRename_ShouldNeverMixCatalogDefinitions(bool join)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("rename-select-race", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var ddl = await database.CreateSessionAsync(CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await SqlAlterTableExecutionTests.ExecuteAsync(ddl, "CREATE TABLE t(id INT PRIMARY KEY, u INT)");
        await SqlAlterTableExecutionTests.ExecuteAsync(ddl, "CREATE INDEX ix_u ON t(u)");
        await SqlAlterTableExecutionTests.ExecuteAsync(ddl, "INSERT INTO t VALUES (1, 7)");
        await SqlAlterTableExecutionTests.ExecuteAsync(ddl, "CREATE TABLE keys(id INT PRIMARY KEY)");
        await SqlAlterTableExecutionTests.ExecuteAsync(ddl, "INSERT INTO keys VALUES (1)");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        int reads = 0, renames = 0, overlaps = 0;
        bool done = false;
        string query = join ? "SELECT * FROM t JOIN keys ON keys.id = t.id WHERE t.id = 1" : "SELECT * FROM t WHERE id = 1";
        async Task RenameAsync()
        {
            await start.Task;
            string current = "u";
            try
            {
                while ((clock.Elapsed < TimeSpan.FromSeconds(1) || renames < 64 || Volatile.Read(ref reads) < 128 || Volatile.Read(ref overlaps) < 8 || current != "u") && clock.Elapsed < TimeSpan.FromSeconds(20))
                {
                    string next = current == "u" ? "v" : "u";
                    var result = await ddl.ExecuteAsync($"ALTER TABLE t RENAME {current} TO {next}", cancellationToken: timeout.Token);
                    result.Status.ShouldBe(DatabaseResultStatus.Success);
                    current = next;
                    Interlocked.Increment(ref renames);
                }
                current.ShouldBe("u");
            }
            finally { Volatile.Write(ref done, true); }
        }
        async Task ReadAsync()
        {
            await start.Task;
            while (!Volatile.Read(ref done) && clock.Elapsed < TimeSpan.FromSeconds(20))
            {
                int before = Volatile.Read(ref renames);
                try
                {
                    await using var result = (await reader.ExecuteAsync(query, cancellationToken: timeout.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
                    int rows = 0;
                    await foreach (var row in result.GetRowsAsync(timeout.Token))
                    {
                        row.GetValue(0).ShouldBe(1);
                        row.GetValue(1).ShouldBe(7);
                        rows++;
                    }
                    rows.ShouldBe(1);
                    Interlocked.Increment(ref reads);
                }
                catch (DatabaseException error) when (join && error.Message.Contains("changed while the statement was waiting", StringComparison.Ordinal))
                {
                    // Joins recheck after taking Shared locks. A rename that won that wait
                    // requires retry; an unknown index key (COHSQLE015) must never escape.
                    error.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Code == "COHSQLE015");
                }
                if (Volatile.Read(ref renames) > before) { Interlocked.Increment(ref overlaps); }
            }
        }

        // Act: minimum duration plus proved work and overlapping publications, with a ceiling.
        var writer = Task.Run(RenameAsync, timeout.Token);
        var reading = Task.Run(ReadAsync, timeout.Token);
        start.SetResult();
        await Task.WhenAll(writer, reading);

        // Assert
        renames.ShouldBeGreaterThanOrEqualTo(64);
        reads.ShouldBeGreaterThanOrEqualTo(128);
        overlaps.ShouldBeGreaterThanOrEqualTo(8);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RENAME locks: child requests follow ObjectId despite reversed catalog order")]
    public async Task ExecuteAsync_SharedChildren_ShouldLockInObjectIdOrder()
    {
        // Arrange: reusing an earlier directory slot puts higher-id c2 before c1 in catalog order.
        await using var engine = SqlDatabaseEngine.Create("rename-lock-order", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlAlterTableExecutionTests.ExecuteAsync(session, "CREATE TABLE placeholder(id INT)");
        await SqlAlterTableExecutionTests.ExecuteAsync(session, "CREATE TABLE p1(id INT PRIMARY KEY)");
        await SqlAlterTableExecutionTests.ExecuteAsync(session, "CREATE TABLE p2(id INT PRIMARY KEY)");
        await SqlAlterTableExecutionTests.ExecuteAsync(session, "CREATE TABLE c1(a INT REFERENCES p1(id), b INT REFERENCES p2(id))");
        await SqlAlterTableExecutionTests.ExecuteAsync(session, "DROP TABLE placeholder");
        await SqlAlterTableExecutionTests.ExecuteAsync(session, "CREATE TABLE c2(a INT REFERENCES p1(id), b INT REFERENCES p2(id))");
        database.Catalog.TryGetTable("dbo", "c1", out var c1).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "c2", out var c2).ShouldBeTrue();
        c1.ObjectId.ShouldBeLessThan(c2.ObjectId);
        database.Catalog.Tables.Where(table => table.Name is "c1" or "c2").Select(table => table.Name).ShouldBe(new[] { "c2", "c1" });
        var coordinator = database.Coordinator;
        var blocker = await coordinator.BeginAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        var first = await coordinator.BeginAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        var probe = await coordinator.BeginAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        try
        {
            await coordinator.LockManager.AcquireAsync(blocker.Sequence, LockResource.Object(c2.ObjectId), LockMode.Exclusive, TestTimeout.Token());
            var executor = new SqlQueryExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);

            // Act: p1 must already hold c1 while it waits for c2.
            var pending = executor.ExecuteAsync(SqlQueryRequest.FromSql("ALTER TABLE p1 RENAME TO q1"), new SqlStatementContext(first, coordinator), TestTimeout.Token());
            pending.IsCompleted.ShouldBeFalse();

            // Assert: reversing or removing the sort lets this probe acquire c1 and fails here.
            coordinator.LockManager.TryAcquire(probe.Sequence, LockResource.Object(c1.ObjectId), LockMode.Exclusive).ShouldBeFalse();
            await coordinator.CommitAsync(blocker, TestTimeout.Token());
            await pending;
            await coordinator.CommitAsync(first, TestTimeout.Token());
            await SqlAlterTableExecutionTests.ExecuteAsync(session, "ALTER TABLE p2 RENAME TO q2");
        }
        finally
        {
            foreach (var context in new[] { blocker, first, probe })
            {
                if (context.State == TransactionState.Active) { await coordinator.RollbackAsync(context, CancellationToken.None); }
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SELECT planning: a rename between table and index reads replans before execution")]
    public async Task ExecuteAsync_RenameDuringPlanning_ShouldDiscardMixedGenerationAndRetry()
    {
        // Arrange: constant folding pauses after table binding and before index lookup.
        using var gate = new SqlBindingGate();
        var builder = SqlDatabaseEngine.CreateBuilder("rename-plan-generation");
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("plan_gate", gate.Invoke, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var ddl = await database.CreateSessionAsync(CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await SqlAlterTableExecutionTests.ExecuteAsync(ddl, "CREATE TABLE t(id INT PRIMARY KEY, u INT)");
        await SqlAlterTableExecutionTests.ExecuteAsync(ddl, "CREATE INDEX ix_u ON t(u)");
        await SqlAlterTableExecutionTests.ExecuteAsync(ddl, "INSERT INTO t VALUES (1, 7)");
        List<object?[]>? rows = null;
        gate.Arm();
        var selecting = new SqlBindingCaller(async () =>
            rows = await SqlAlterTableExecutionTests.RowsAsync(reader, "SELECT id, plan_gate(1) FROM t WHERE id = 1"));
        Exception? failure = null;
        try
        {
            await gate.WaitUntilEnteredAsync();

            // Act: the first plan now holds old columns and will read renamed index metadata.
            await SqlAlterTableExecutionTests.ExecuteAsync(ddl, "ALTER TABLE t RENAME u TO v");
        }
        finally
        {
            gate.Release();
            failure = await selecting.JoinAsync();
        }

        // Assert: the mixed plan's COHSQLE015 is discarded and a fresh binding produces the row.
        failure.ShouldBeNull();
        gate.Calls.ShouldBeGreaterThanOrEqualTo(2);
        rows.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 1 });
    }

}
