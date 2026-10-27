using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlTableRewriteTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Rewrite: child DELETE rebinds its released parent before a rollback can restore an orphan (#1265)")]
    public async Task RewriteTableAsync_ChildDeleteBeforeParentGrant_ShouldRetryAndPreventOrphan()
    {
        // Arrange: the second discovery is the outgoing parent of the deleted child.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var prepared = new ManualResetEventSlim();
        using var publish = new ManualResetEventSlim();
        using var discovered = new ManualResetEventSlim();
        using var grant = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("rewrite-child-delete-grant", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var child = await database.CreateSessionAsync(timeout.Token);
        await using var parent = await database.CreateSessionAsync(timeout.Token);
        await PrepareAsync(session, timeout.Token);
        await ExecAsync(session, "CREATE TABLE child (id INT REFERENCES t(id))", timeout.Token);
        await ExecAsync(session, "INSERT INTO child VALUES (1)", timeout.Token);
        await using var deletion = await child.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        int discoveries = 0;
        SqlPlanExecutor.RewriteStage = (stage, _) =>
        {
            if (stage == "Prepared") { prepared.Set(); publish.Wait(timeout.Token); }
        };
        SqlPlanExecutor.ReferenceIntentsDiscovered = table =>
        {
            if (table.Name == "child" && Interlocked.Increment(ref discoveries) == 2)
            {
                discovered.Set();
                grant.Wait(timeout.Token);
            }
        };
        var rewrite = database.RewriteTableAsync(Table(database), timeout.Token).AsTask();
        Task? deleting = null;
        Task? deletingParent = null;
        try
        {
            prepared.Wait(timeout.Token);
            deleting = DedicatedTestTask.Run(() => ExecAsync(child, "DELETE FROM child WHERE id=1", timeout.Token), timeout.Token);
            discovered.Wait(timeout.Token);

            // Act: publish O2 before granting the child's parent IS and shared row lock.
            publish.Set();
            await rewrite;
            grant.Set();
            await deleting;
            await using var parentTransaction = await parent.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
            using var listener = new LockWaitListener(database.Name);
            var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.Observed = written =>
            {
                if (written.EventName == "LockWaitStart" && written.Payload is { Count: > 1 } payload &&
                    Equals(payload[1], (long)parentTransaction.Context.Sequence.Value)) { waited.TrySetResult(); }
            };
            deletingParent = ExecAsync(parent, "DELETE FROM t WHERE id=1", timeout.Token);
            (await Task.WhenAny(waited.Task, deletingParent).WaitAsync(timeout.Token)).ShouldBeSameAs(waited.Task);
            await deletion.RollbackAsync(timeout.Token);
            await Should.ThrowAsync<SqlConstraintViolationException>(async () => await deletingParent);
            await parentTransaction.RollbackAsync(timeout.Token);

            // Assert: rollback restored the child, so the parent must still exist.
            discoveries.ShouldBeGreaterThanOrEqualTo(3);
            (await RowsAsync(session, "SELECT * FROM child", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
            (await RowsAsync(session, "SELECT * FROM t", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
            _output.WriteLine($"child DELETE discoveries={discoveries}; parent DELETE waited and refused after child rollback");
        }
        finally
        {
            SqlPlanExecutor.ReferenceIntentsDiscovered = null;
            SqlPlanExecutor.RewriteStage = null;
            publish.Set();
            grant.Set();
            await rewrite;
            if (deleting is not null) { try { await deleting; } catch (DatabaseException) { } }
            await deletion.RollbackAsync(CancellationToken.None);
            if (deletingParent is not null) { try { await deletingParent; } catch (DatabaseException) { } }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Rewrite: parent DELETE refreshes an incoming child replaced before intent grant (#1265)")]
    public async Task RewriteTableAsync_ParentDeleteDiscoversRewritingChild_ShouldRetryAndCascadeCurrentOwner()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var prepared = new ManualResetEventSlim();
        using var publish = new ManualResetEventSlim();
        using var discovered = new ManualResetEventSlim();
        using var grant = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("rewrite-incoming-child-grant", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var parent = await database.CreateSessionAsync(timeout.Token);
        await PrepareAsync(session, timeout.Token);
        await ExecAsync(session, "CREATE TABLE child (id INT REFERENCES t(id) ON DELETE CASCADE)", timeout.Token);
        await ExecAsync(session, "INSERT INTO child VALUES (1)", timeout.Token);
        var oldChild = database.Catalog.Tables.Single(table => table.Name == "child");
        int discoveries = 0;
        SqlPlanExecutor.RewriteStage = (stage, _) =>
        {
            if (stage == "Prepared") { prepared.Set(); publish.Wait(timeout.Token); }
        };
        SqlPlanExecutor.ReferenceIntentsDiscovered = table =>
        {
            if (table.Name == "t" && Interlocked.Increment(ref discoveries) == 1)
            {
                discovered.Set();
                grant.Wait(timeout.Token);
            }
        };
        var rewrite = database.RewriteTableAsync(oldChild, timeout.Token).AsTask();
        Task? deleting = null;
        try
        {
            prepared.Wait(timeout.Token);
            deleting = DedicatedTestTask.Run(() => ExecAsync(parent, "DELETE FROM t WHERE id=1", timeout.Token), timeout.Token);
            discovered.Wait(timeout.Token);

            // Act
            publish.Set();
            await rewrite;
            grant.Set();
            await deleting;

            // Assert: the cascade must reach O2, even though discovery first bound O1.
            discoveries.ShouldBeGreaterThanOrEqualTo(2);
            database.Catalog.Tables.Single(table => table.Name == "child").ObjectId.ShouldNotBe(oldChild.ObjectId);
            (await RowsAsync(session, "SELECT * FROM child", timeout.Token)).ShouldBeEmpty();
            (await RowsAsync(session, "SELECT * FROM t", timeout.Token)).ShouldBeEmpty();
            _output.WriteLine($"parent DELETE discoveries={discoveries}; cascade removed the replacement child row");
        }
        finally
        {
            SqlPlanExecutor.ReferenceIntentsDiscovered = null;
            SqlPlanExecutor.RewriteStage = null;
            publish.Set();
            grant.Set();
            await rewrite;
            if (deleting is not null) { try { await deleting; } catch (DatabaseException) { } }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Rewrite: an FK parent replaced between discovery and lock grant is rebound")]
    public async Task RewriteTableAsync_ParentSwappedBeforeIntentGrant_ShouldRetryAndPreventOrphan()
    {
        // Arrange: pause INSERT after discovering O1 while rewrite owns its X lock.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var prepared = new ManualResetEventSlim();
        using var publish = new ManualResetEventSlim();
        using var discovered = new ManualResetEventSlim();
        using var grant = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("rewrite-fk-grant", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var child = await database.CreateSessionAsync(timeout.Token);
        await PrepareAsync(session, timeout.Token);
        await ExecAsync(session, "CREATE TABLE child (id INT REFERENCES t(id))", timeout.Token);
        int discoveries = 0;
        SqlPlanExecutor.RewriteStage = (stage, _) =>
        {
            if (stage == "Prepared") { prepared.Set(); publish.Wait(timeout.Token); }
        };
        SqlPlanExecutor.ReferenceIntentsDiscovered = table =>
        {
            if (table.Name == "child" && Interlocked.Increment(ref discoveries) == 1)
            {
                discovered.Set();
                grant.Wait(timeout.Token);
            }
        };
        var rewrite = database.RewriteTableAsync(Table(database), timeout.Token).AsTask();
        Task? inserting = null;
        try
        {
            prepared.Wait(timeout.Token);
            inserting = DedicatedTestTask.Run(() => ExecAsync(child, "INSERT INTO child VALUES (1)", timeout.Token), timeout.Token);
            discovered.Wait(timeout.Token);

            // Act: retire O1, delete K from O2, then let INSERT lock its discovered owner.
            publish.Set();
            await rewrite;
            await ExecAsync(session, "DELETE FROM t WHERE id=1", timeout.Token);
            grant.Set();
            DatabaseException? failure = null;
            try { await inserting; }
            catch (DatabaseException error) { failure = error; }

            // Assert: retry preparation, then abort the snapshot that predates O2.
            discoveries.ShouldBeGreaterThanOrEqualTo(2);
            failure.ShouldBeOfType<DatabaseTransactionAbortedException>().Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04006");
            _output.WriteLine($"child INSERT discoveries={discoveries}; retryable refusal={failure.Diagnostics[0].Code}");
            (await RowsAsync(session, "SELECT * FROM child", timeout.Token)).ShouldBeEmpty();
            (await RowsAsync(session, "SELECT * FROM t", timeout.Token)).ShouldBeEmpty();
        }
        finally
        {
            SqlPlanExecutor.ReferenceIntentsDiscovered = null;
            SqlPlanExecutor.RewriteStage = null;
            publish.Set();
            grant.Set();
            await rewrite;
            if (inserting is not null) { try { await inserting; } catch (DatabaseException) { /* Expected coded refusal. */ } }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rewrite: a retained parent snapshot fails FK DML with a retryable abort")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RewriteTableAsync_ParentUpdatedAfterBegin_ShouldAbortStaleForeignKeySnapshot(bool rewrite)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("rewrite-fk-snapshot", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var stale = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t (id INT PRIMARY KEY, value INT)", timeout.Token);
        await ExecAsync(session, "CREATE TABLE child (id INT REFERENCES t(id))", timeout.Token);
        await ExecAsync(session, "INSERT INTO t VALUES (1,10)", timeout.Token);
        await using var snapshot = await stale.BeginTransactionAsync(timeout.Token);
        await ExecAsync(session, "UPDATE t SET value=20 WHERE id=1", timeout.Token);
        if (rewrite) { await database.RewriteTableAsync(Table(database), timeout.Token); }
        (await RowsAsync(stale, "SELECT * FROM t", timeout.Token)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 10 });

        // Act
        var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(() => ExecAsync(stale, "INSERT INTO child VALUES (1)", timeout.Token));

        // Assert: rewrite preserves the preexisting retryable concurrency classification.
        if (rewrite) { failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04006"); }
        else { failure.InnerException.ShouldBeOfType<TransactionAbortedException>(); }
        (await RowsAsync(session, "SELECT * FROM child", timeout.Token)).ShouldBeEmpty();
        await ExecAsync(session, "INSERT INTO child VALUES (1)", timeout.Token);
    }
}
