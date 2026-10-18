using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

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

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - RENAME concurrency: two sessions repeatedly rename mutual FK tables")]
    public async Task ExecuteAsync_TwoSessionsRenamingRelatedTables_ShouldMakeProgressAndPreserveReferences()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("rename-sessions", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db", cancellationToken: CancellationToken.None);
        await using var one = await database.CreateSessionAsync(CancellationToken.None);
        await using var two = await database.CreateSessionAsync(CancellationToken.None);
        await SqlAlterTableExecutionTests.ExecuteAsync(one, "CREATE TABLE a(id INT PRIMARY KEY, bid INT)");
        await SqlAlterTableExecutionTests.ExecuteAsync(one, "CREATE TABLE b(id INT PRIMARY KEY, aid INT REFERENCES a(id))");
        await SqlAlterTableExecutionTests.ExecuteAsync(one, "ALTER TABLE a ADD CONSTRAINT fk_b FOREIGN KEY(bid) REFERENCES b(id)");
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
                catch (DatabaseTransactionDeadlockException) { }
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
        a.Constraints.Single().ReferencedTable.ShouldBe("b");
        b.Constraints.Single().ReferencedTable.ShouldBe("a");
        await SqlAlterTableExecutionTests.ExecuteAsync(one, "INSERT INTO a VALUES (1, NULL)");
        await SqlAlterTableExecutionTests.ExecuteAsync(two, "INSERT INTO b VALUES (2, 1)");
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
}
