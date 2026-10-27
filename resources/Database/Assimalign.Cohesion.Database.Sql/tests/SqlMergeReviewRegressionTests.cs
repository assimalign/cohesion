using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SQL upsert concurrency")]
public sealed class SqlMergeReviewRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: cascaded rekeys still validate old parent keys (#1715)")]
    [InlineData("self")]
    [InlineData("NO ACTION")]
    [InlineData("RESTRICT")]
    [InlineData("SET NULL")]
    public async Task ExecuteAsync_CascadedRekeyWithOldKeyChild_ShouldRefuseAtomically(string policy)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-old-key", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES t(id) ON DELETE CASCADE)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,NULL),(2,1)" + (policy == "self" ? ",(3,2)" : ""), cancellationToken: timeout.Token);
        if (policy != "self")
        {
            await session.ExecuteAsync("CREATE TABLE child(id INT,pid INT REFERENCES t(id) ON DELETE " + policy + ")", cancellationToken: timeout.Token);
            await session.ExecuteAsync("INSERT INTO child VALUES(3,2)", cancellationToken: timeout.Token);
        }

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "MERGE INTO t USING (SELECT 1 AS id UNION ALL SELECT 2) s ON t.id=s.id " +
            "WHEN MATCHED AND t.id=1 THEN DELETE WHEN MATCHED THEN UPDATE SET id=20", cancellationToken: timeout.Token));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03039");
        (await RowsAsync(session, "SELECT id,pid FROM t ORDER BY id", timeout.Token)).ShouldBe(policy == "self" ? ["1,", "2,1", "3,2"] : ["1,", "2,1"]);
        if (policy != "self") { (await RowsAsync(session, "SELECT id,pid FROM child", timeout.Token)).ShouldBe(["3,2"]); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: immediate uniqueness precedes final foreign keys (#1715)")]
    public async Task ExecuteAsync_UniqueAndForeignKeyViolation_ShouldReportUniqueFirst()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-precedence", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES t(id))", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,NULL),(2,NULL)", cancellationToken: timeout.Token);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "MERGE INTO t USING (SELECT 1 AS id) s ON t.id=s.id WHEN MATCHED THEN UPDATE SET id=2,pid=99", cancellationToken: timeout.Token));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03037");
        (await RowsAsync(session, "SELECT id,pid FROM t ORDER BY id", timeout.Token)).ShouldBe(["1,", "2,"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: host rollback and abandoned owners keep ordinary uncoded aborts (#1715)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_HostEndsSelectedTransaction_ShouldKeepUncodedAbort(bool abandoned)
    {
        // Arrange: end the manager transaction after selection while the statement pin lives.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-host-rollback", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES(1)", cancellationToken: timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        int rollbacks = 0;
        SqlPlanExecutor.MergeActionsSelected = _ =>
        {
            if (abandoned) { AbandonPending(database.Coordinator.LockManager, transaction.Context.Sequence); }
            else { database.Coordinator.Manager.RollbackAsync(transaction.Context, timeout.Token).AsTask().GetAwaiter().GetResult(); }
            rollbacks++;
        };
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await session.ExecuteAsync(
                "MERGE INTO t USING (SELECT 1 AS id) s ON t.id=s.id WHEN MATCHED THEN DELETE", cancellationToken: timeout.Token));

            // Assert
            rollbacks.ShouldBe(1);
            failure.Diagnostics.ShouldBeEmpty();
            await using var observer = await database.CreateSessionAsync(timeout.Token);
            (await RowsAsync(observer, "SELECT id FROM t", timeout.Token)).ShouldBe(["1"]);
        }
        finally { SqlPlanExecutor.MergeActionsSelected = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: moved then cascaded children lock their stored parent until transaction end (#1715)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_MovedThenCascadedChild_ShouldBlockOldParentDelete(bool commit)
    {
        // Arrange: T1 moves row 2 from parent 5 to parent 1, then cascades it away.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-old-parent-lock", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var mover = await database.CreateSessionAsync(timeout.Token);
        await using var deleter = await database.CreateSessionAsync(timeout.Token);
        await mover.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES t(id) ON DELETE CASCADE)", cancellationToken: timeout.Token);
        await mover.ExecuteAsync("INSERT INTO t VALUES(1,NULL),(5,NULL),(2,5)", cancellationToken: timeout.Token);
        await using var move = await mover.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        (await mover.ExecuteAsync("MERGE INTO t USING (SELECT 1 AS id UNION ALL SELECT 2) s ON t.id=s.id " +
            "WHEN MATCHED AND t.id=1 THEN DELETE WHEN MATCHED THEN UPDATE SET pid=1", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(2);
        await using var delete = await deleter.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        using var waits = new LockWaitListener(database.Name);
        var waiting = waits.WaitForWaitAsync(delete.Context.Sequence, timeout.Token);
        var pending = deleter.ExecuteAsync("DELETE FROM t WHERE id=5", cancellationToken: timeout.Token).AsTask();
        try
        {
            // Act: a recorded queue event must precede T2's completion; no elapsed-time assertion.
            (await Task.WhenAny(waiting, pending)).ShouldBeSameAs(waiting);
            await waiting;
            pending.IsCompleted.ShouldBeFalse();
            if (commit) { await move.CommitAsync(timeout.Token); }
            else { await move.RollbackAsync(timeout.Token); }
            (await pending).AffectedCount.ShouldBe(1);
            await delete.CommitAsync(timeout.Token);

            // Assert: rollback restores row 2 before T2 cascades it; commit leaves no child to restore.
            (await RowsAsync(mover, "SELECT id,pid FROM t ORDER BY id", timeout.Token)).ShouldBe(commit ? [] : ["1,"]);
            (await RowsAsync(mover, "SELECT c.id FROM t c LEFT JOIN t p ON c.pid=p.id WHERE c.pid IS NOT NULL AND p.id IS NULL", timeout.Token)).ShouldBeEmpty();
        }
        finally
        {
            if (move.State == TransactionState.Active) { await move.RollbackAsync(CancellationToken.None); }
            timeout.Cancel();
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            await waiting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: immediate uniqueness precedes SET action constraint failures (#1715)")]
    [InlineData("SET NULL", "not-null", true)]
    [InlineData("SET NULL", "not-null", false)]
    [InlineData("SET DEFAULT", "not-null", true)]
    [InlineData("SET DEFAULT", "not-null", false)]
    [InlineData("SET DEFAULT", "check", true)]
    [InlineData("SET DEFAULT", "check", false)]
    [InlineData("SET DEFAULT", "domain", true)]
    [InlineData("SET DEFAULT", "domain", false)]
    [InlineData("SET DEFAULT", "identity", true)]
    [InlineData("SET DEFAULT", "identity", false)]
    public async Task ExecuteAsync_UniqueAndSetActionViolation_ShouldReportUniqueFirst(string policy, string constraint, bool duplicate)
    {
        // Arrange: the duplicate is direct; the other failure is an AFTER-style write.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-set-precedence", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES(1),(2),(3)", cancellationToken: timeout.Token);
        if (constraint == "domain")
        { await session.ExecuteAsync("CREATE DOMAIN positive_id AS INT CHECK(VALUE>0)", cancellationToken: timeout.Token); }
        string column = constraint switch
        {
            "not-null" => "INT NOT NULL",
            "check" => "INT DEFAULT 0 CHECK(pid>0)",
            "domain" => "positive_id DEFAULT 0",
            _ => "INT GENERATED ALWAYS AS IDENTITY",
        };
        await session.ExecuteAsync("CREATE TABLE child(pid " + column + " REFERENCES t(id) ON DELETE " + policy + ")", cancellationToken: timeout.Token);
        await session.ExecuteAsync(constraint == "identity" ? "INSERT INTO child DEFAULT VALUES" : "INSERT INTO child VALUES(1)", cancellationToken: timeout.Token);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "MERGE INTO t USING (SELECT 1 AS id UNION ALL SELECT 2) s ON t.id=s.id " +
            "WHEN MATCHED AND t.id=1 THEN DELETE WHEN MATCHED THEN UPDATE SET id=" + (duplicate ? "3" : "4"), cancellationToken: timeout.Token));

        // Assert: without a duplicate, the saved constraint retains its code and atomicity.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(duplicate ? "CS03037" : constraint switch
        {
            "not-null" => policy == "SET NULL" ? "CS03039" : "CS03024",
            "identity" => "CS03025",
            _ => "CS03038",
        });
        (await RowsAsync(session, "SELECT id FROM t ORDER BY id", timeout.Token)).ShouldBe(["1", "2", "3"]);
        (await RowsAsync(session, "SELECT pid FROM child", timeout.Token)).ShouldBe(["1"]);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: a saved FK failure does not lock stale validated parents (#1715)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ForeignKeyFailureWithStaleParent_ShouldPreserveStatementError(bool duplicate)
    {
        // Arrange: one outgoing FK resolves a snapshot parent; a later row fails its FK.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-saved-fk", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE p(id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES p(id))", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO p VALUES(10)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,NULL),(2,NULL)", cancellationToken: timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, timeout.Token);
        int removals = 0;
        SqlPlanExecutor.MergeActionsSelected = _ =>
        {
            writer.ExecuteAsync("DELETE FROM p", cancellationToken: timeout.Token).AsTask().GetAwaiter().GetResult();
            removals++;
        };
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "MERGE INTO t USING (SELECT 1 AS id,10 AS pid UNION ALL SELECT 2,99) s ON t.id=s.id " +
                "WHEN MATCHED THEN UPDATE SET pid=s.pid,id=" + (duplicate ? "1" : "t.id"), cancellationToken: timeout.Token));

            // Assert: a parent-version abort must not mask the retained FK or immediate duplicate.
            removals.ShouldBe(1);
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(duplicate ? "CS03037" : "CS03039");
            await transaction.RollbackAsync(timeout.Token);
            (await RowsAsync(writer, "SELECT id,pid FROM t ORDER BY id", timeout.Token)).ShouldBe(["1,", "2,"]);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlPlanExecutor.MergeActionsSelected = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: parent and cascade snapshot conflicts retain plain DML's uncoded abort (#1715)")]
    [InlineData("parent")]
    [InlineData("cascade")]
    public async Task ExecuteAsync_NonTargetVersionConflict_ShouldKeepUncodedAbort(string conflict)
    {
        // Arrange: the transaction snapshot predates the referenced version change.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-nontarget-conflict", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("CREATE TABLE p(id INT PRIMARY KEY)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES p(id) ON DELETE CASCADE)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO p VALUES(1)", cancellationToken: timeout.Token);
        await session.ExecuteAsync("INSERT INTO t VALUES(2,NULL)", cancellationToken: timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, timeout.Token);
        await writer.ExecuteAsync(conflict == "parent" ? "DELETE FROM p" : "INSERT INTO t VALUES(3,1)", cancellationToken: timeout.Token);

        // Act
        string sql = conflict == "parent"
            ? "MERGE INTO t USING (SELECT 2 AS id) s ON t.id=s.id WHEN MATCHED THEN UPDATE SET pid=1"
            : "MERGE INTO p USING (SELECT 1 AS id) s ON p.id=s.id WHEN MATCHED THEN DELETE";
        var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await session.ExecuteAsync(sql, cancellationToken: timeout.Token));

        // Assert: CS04064 belongs to direct target-version rechecks, not shared RI checks.
        failure.Diagnostics.ShouldBeEmpty();
        failure.InnerException.ShouldBeOfType<TransactionAbortedException>();
        await transaction.RollbackAsync(timeout.Token);
        (await RowsAsync(writer, "SELECT id,pid FROM t ORDER BY id", timeout.Token)).ShouldBe(conflict == "parent" ? ["2,"] : ["2,", "3,1"]);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "AbandonPending")]
    private static extern void AbandonPending(LockManager manager, TransactionSequence owner);

    private static async Task<string[]> RowsAsync(SqlDatabaseSession session, string sql, CancellationToken token)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: token)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new System.Collections.Generic.List<string>();
        await foreach (var row in result.GetRowsAsync(token)) { rows.Add(string.Join(',', Enumerable.Range(0, row.FieldCount).Select(row.GetValue))); }
        return rows.ToArray();
    }
}
