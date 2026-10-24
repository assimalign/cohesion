using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlReferenceLockTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Reference locks: DML waiting for adjacent DDL holds no conflicting own-table grant (#1198)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Execute_AdjacentForeignKeyDdl_ShouldCompleteWithoutDeadlock(bool insert, bool addColumn)
    {
        // Arrange: rendezvous at the actual adjacent IS wait, while the DDL owner
        // holds its own X. The real DDL then requests X on the DML target.
        int rounds = 0;
        var timer = Stopwatch.StartNew();
        do
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var engine = SqlDatabaseEngine.Create("reference-lock-order", new());
            var database = await engine.CreateDatabaseAsync("db");
            await using var setup = await database.CreateSessionAsync();
            await using var dml = await database.CreateSessionAsync();
            await setup.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY, cid INT)");
            await setup.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT REFERENCES p(id) ON DELETE CASCADE)");
            await setup.ExecuteAsync("INSERT INTO p VALUES (1,NULL)");
            await setup.ExecuteAsync("INSERT INTO c VALUES (1,1)");
            database.Catalog.TryGetTable("dbo", insert ? "p" : "c", out var adjacent).ShouldBeTrue();
            var owner = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, deadline.Token);
            Task<DatabaseResult>? pending = null;
            using var listener = new LockWaitListener("db");
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.Observed = written =>
            {
                if (written.EventName == "LockWaitStart" && written.Payload is { Count: 4 } payload &&
                    Equals(payload[2], $"Object:{adjacent.ObjectId}") && Equals(payload[3], "IntentShared")) { waiting.TrySetResult(); }
            };
            try
            {
                await database.Coordinator.LockManager.AcquireAsync(owner.Sequence,
                    LockResource.Object(adjacent.ObjectId), LockMode.Exclusive, deadline.Token);
                pending = dml.ExecuteAsync(insert ? "INSERT INTO c VALUES (2,1)" : "DELETE FROM p",
                    cancellationToken: deadline.Token).AsTask();
                await waiting.Task.WaitAsync(deadline.Token);
                await using var ddl = database.CreateSchemaSession("lock-test", deadline.Token, owner.Sequence);
                string table = insert ? "p" : "c", parent = insert ? "c" : "p", column = insert ? "cid" : "pid";
                string sql = addColumn
                    ? $"ALTER TABLE {table} ADD COLUMN extra INT REFERENCES {parent}(id) ON DELETE CASCADE"
                    : $"ALTER TABLE {table} ADD CONSTRAINT fk_extra FOREIGN KEY({column}) REFERENCES {parent}(id) ON DELETE CASCADE";

                // Act
                (await ddl.ExecuteAsync(sql, cancellationToken: deadline.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
                await database.Coordinator.RollbackAsync(owner, deadline.Token);
                var result = await pending;

                // Assert: both statements completed, and the waiting DML ran once.
                result.AffectedCount.ShouldBe(1);
                (await setup.ExecuteAsync("DELETE FROM c")).AffectedCount.ShouldBe(insert ? 2 : 0);
                rounds++;
            }
            finally
            {
                deadline.Cancel();
                if (owner.State == TransactionState.Active) { await database.Coordinator.RollbackAsync(owner, CancellationToken.None); }
                if (pending is not null) { try { await pending; } catch (DatabaseException) { } catch (OperationCanceledException) { } }
            }
        } while ((timer.Elapsed < TimeSpan.FromSeconds(1) || rounds < 3) && timer.Elapsed < TimeSpan.FromSeconds(30));
        rounds.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Reference locks: adjacent definition changes rebuild before reading rows (#1198)")]
    [InlineData("parent-column")]
    [InlineData("child-column")]
    [InlineData("child-drop")]
    [InlineData("child-constraint")]
    [InlineData("parent-drop")]
    public async Task Execute_AdjacentDefinitionChangedDuringWait_ShouldRebuildAndComplete(string change)
    {
        // Arrange
        int rounds = 0;
        var timer = Stopwatch.StartNew();
        do
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var engine = SqlDatabaseEngine.Create("reference-refresh", new());
            var database = await engine.CreateDatabaseAsync("db");
            await using var setup = await database.CreateSessionAsync();
            await using var dml = await database.CreateSessionAsync();
            await setup.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
            await setup.ExecuteAsync("CREATE TABLE c (id INT PRIMARY KEY, pid INT, CONSTRAINT fk FOREIGN KEY(pid) REFERENCES p(id) ON DELETE CASCADE)");
            await setup.ExecuteAsync("INSERT INTO p VALUES (1)");
            await setup.ExecuteAsync("INSERT INTO c VALUES (1,1)");
            bool insert = change.StartsWith("parent-", StringComparison.Ordinal);
            database.Catalog.TryGetTable("dbo", insert ? "p" : "c", out var adjacent).ShouldBeTrue();
            var owner = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, deadline.Token);
            Task<DatabaseResult>? pending = null;
            using var listener = new LockWaitListener("db");
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.Observed = written =>
            {
                if (written.EventName == "LockWaitStart" && written.Payload is { Count: 4 } payload &&
                    Equals(payload[2], $"Object:{adjacent.ObjectId}") && Equals(payload[3], "IntentShared")) { waiting.TrySetResult(); }
            };
            try
            {
                await database.Coordinator.LockManager.AcquireAsync(owner.Sequence,
                    LockResource.Object(adjacent.ObjectId), LockMode.Exclusive, deadline.Token);
                pending = dml.ExecuteAsync(insert ? "INSERT INTO c VALUES (2,1)" : "DELETE FROM p",
                    cancellationToken: deadline.Token).AsTask();
                await waiting.Task.WaitAsync(deadline.Token);
                await using var ddl = database.CreateSchemaSession("lock-test", deadline.Token, owner.Sequence);
                string sql = change switch
                {
                    "parent-column" => "ALTER TABLE p ADD COLUMN extra INT",
                    "parent-drop" => "DROP TABLE p",
                    "child-column" => "ALTER TABLE c ADD COLUMN extra INT",
                    "child-drop" => "DROP TABLE c",
                    _ => "ALTER TABLE c DROP CONSTRAINT fk"
                };

                // Act
                if (change == "parent-drop")
                {
                    (await ddl.ExecuteAsync("ALTER TABLE c DROP CONSTRAINT fk", cancellationToken: deadline.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
                }
                (await ddl.ExecuteAsync(sql, cancellationToken: deadline.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
                await database.Coordinator.RollbackAsync(owner, deadline.Token);

                // Assert
                if (change == "parent-drop")
                {
                    var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
                    failure.Diagnostics.Count.ShouldBe(1);
                    failure.Diagnostics[0].Code.ShouldBe("CS04006");
                    (await setup.ExecuteAsync("DELETE FROM c")).AffectedCount.ShouldBe(1);
                }
                else
                {
                    (await pending).AffectedCount.ShouldBe(1);
                }
                if (change != "child-drop" && change != "parent-drop")
                {
                    (await setup.ExecuteAsync("DELETE FROM c")).AffectedCount.ShouldBe(insert ? 2 : change == "child-constraint" ? 1 : 0);
                }
                rounds++;
            }
            finally
            {
                deadline.Cancel();
                if (owner.State == TransactionState.Active) { await database.Coordinator.RollbackAsync(owner, CancellationToken.None); }
                if (pending is not null) { try { await pending; } catch (DatabaseException) { } catch (OperationCanceledException) { } }
            }
        } while ((timer.Elapsed < TimeSpan.FromSeconds(1) || rounds < 3) && timer.Elapsed < TimeSpan.FromSeconds(30));
        rounds.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Reference locks: target-lock waits refresh new edges or return a coded plan retry (#1198)")]
    [InlineData("incoming")]
    [InlineData("column")]
    [InlineData("drop")]
    [InlineData("recreate")]
    public async Task Execute_TargetDefinitionOrIncomingEdgeChanged_ShouldRevalidateUnderOwnLock(string change)
    {
        // Arrange
        int rounds = 0;
        var timer = Stopwatch.StartNew();
        do
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var engine = SqlDatabaseEngine.Create("reference-target-refresh", new());
            var database = await engine.CreateDatabaseAsync("db");
            await using var setup = await database.CreateSessionAsync();
            await using var dml = await database.CreateSessionAsync();
            await setup.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)");
            await setup.ExecuteAsync("CREATE TABLE c (pid INT)");
            await setup.ExecuteAsync("INSERT INTO p VALUES (1)");
            await setup.ExecuteAsync("INSERT INTO c VALUES (1)");
            database.Catalog.TryGetTable("dbo", "p", out var parent).ShouldBeTrue();
            var owner = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, deadline.Token);
            Task<DatabaseResult>? pending = null;
            using var listener = new LockWaitListener("db");
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.Observed = written =>
            {
                if (written.EventName == "LockWaitStart" && written.Payload is { Count: 4 } payload &&
                    Equals(payload[2], $"Object:{parent.ObjectId}") && Equals(payload[3], "IntentExclusive")) { waiting.TrySetResult(); }
            };
            try
            {
                await database.Coordinator.LockManager.AcquireAsync(owner.Sequence,
                    LockResource.Object(parent.ObjectId), LockMode.Exclusive, deadline.Token);
                pending = dml.ExecuteAsync("DELETE FROM p", cancellationToken: deadline.Token).AsTask();
                await waiting.Task.WaitAsync(deadline.Token);
                await using var ddl = database.CreateSchemaSession("lock-test", deadline.Token, owner.Sequence);

                // Act
                string sql = change switch
                {
                    "incoming" => "ALTER TABLE c ADD CONSTRAINT fk FOREIGN KEY(pid) REFERENCES p(id)",
                    "column" => "ALTER TABLE p ADD COLUMN extra INT",
                    _ => "DROP TABLE p"
                };
                (await ddl.ExecuteAsync(sql, cancellationToken: deadline.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
                if (change == "recreate") { await ddl.ExecuteAsync("CREATE TABLE p (id INT PRIMARY KEY)", cancellationToken: deadline.Token); }
                await database.Coordinator.RollbackAsync(owner, deadline.Token);

                // Assert
                if (change != "incoming")
                {
                    var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
                    failure.Diagnostics.Count.ShouldBe(1);
                    failure.Diagnostics[0].Code.ShouldBe("CS04006");
                }
                else
                {
                    var failure = await SqlConstraintAssertions.ThrowAsync(async () => await pending);
                    failure.ConstraintName.ShouldBe("fk");
                }
                (await setup.ExecuteAsync("DELETE FROM c")).AffectedCount.ShouldBe(1);
                if (change != "drop") { (await setup.ExecuteAsync("DELETE FROM p")).AffectedCount.ShouldBe(change == "recreate" ? 0 : 1); }
                rounds++;
            }
            finally
            {
                deadline.Cancel();
                if (owner.State == TransactionState.Active) { await database.Coordinator.RollbackAsync(owner, CancellationToken.None); }
                if (pending is not null) { try { await pending; } catch (DatabaseException) { } catch (OperationCanceledException) { } }
            }
        } while ((timer.Elapsed < TimeSpan.FromSeconds(1) || rounds < 3) && timer.Elapsed < TimeSpan.FromSeconds(30));
        rounds.ShouldBeGreaterThanOrEqualTo(3);
    }
}
