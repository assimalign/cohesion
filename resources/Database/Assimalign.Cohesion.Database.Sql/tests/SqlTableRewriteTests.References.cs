using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlTableRewriteTests
{
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
            var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await inserting);

            // Assert: retry preparation, then abort the snapshot that predates O2.
            discoveries.ShouldBeGreaterThanOrEqualTo(2);
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04006");
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
