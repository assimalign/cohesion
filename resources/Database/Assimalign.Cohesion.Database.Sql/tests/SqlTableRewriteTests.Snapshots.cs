using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlTableRewriteTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Rewrite: renaming the compact parent preserves the retryable snapshot refusal (#1265)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RewriteTableAsync_RenamedReplacement_ShouldAbortStaleForeignKeySnapshot(bool twice)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("rewrite-renamed-parent", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var stale = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t (id INT PRIMARY KEY, value INT)", timeout.Token);
        await ExecAsync(session, "CREATE TABLE child (id INT REFERENCES t(id))", timeout.Token);
        await ExecAsync(session, "INSERT INTO t VALUES (1,10)", timeout.Token);
        await using var snapshot = await stale.BeginTransactionAsync(timeout.Token);
        await ExecAsync(session, "UPDATE t SET value=20 WHERE id=1", timeout.Token);
        await database.RewriteTableAsync(Table(database), timeout.Token);
        if (twice) { await database.RewriteTableAsync(Table(database), timeout.Token); }
        await ExecAsync(session, "ALTER TABLE t RENAME TO t2", timeout.Token);

        // Act
        var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(
            () => ExecAsync(stale, "INSERT INTO child VALUES (1)", timeout.Token));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04006");
        (await RowsAsync(session, "SELECT * FROM child", timeout.Token)).ShouldBeEmpty();
        await snapshot.RollbackAsync(timeout.Token);
        await ExecAsync(stale, "INSERT INTO child VALUES (1)", timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Rewrite: read committed ON CONFLICT uses its fresh parent probe snapshot (#1265)")]
    public async Task RewriteTableAsync_ReadCommittedUpsert_ShouldProbeFreshSnapshot()
    {
        // Arrange: stop the statement after its original snapshot and parent discovery.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var discovered = new ManualResetEventSlim();
        using var grant = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("rewrite-upsert-snapshot", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var child = await database.CreateSessionAsync(timeout.Token);
        await PrepareAsync(session, timeout.Token);
        await ExecAsync(session, "CREATE TABLE child (id INT PRIMARY KEY, pid INT REFERENCES t(id), note INT)", timeout.Token);
        await ExecAsync(session, "INSERT INTO child VALUES (2,1,0)", timeout.Token);
        await using var transaction = await child.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        int discoveries = 0;
        SqlPlanExecutor.ReferenceIntentsDiscovered = table =>
        {
            if (table.Name == "child" && Interlocked.Increment(ref discoveries) == 1)
            {
                discovered.Set();
                grant.Wait(timeout.Token);
            }
        };
        var pending = DedicatedTestTask.Run(() => ExecAsync(child,
            "INSERT INTO child VALUES (2,1,1) ON CONFLICT(id) DO UPDATE SET note=EXCLUDED.note", timeout.Token), timeout.Token);
        try
        {
            discovered.Wait(timeout.Token);

            // Act
            await database.RewriteTableAsync(Table(database), timeout.Token);
            grant.Set();
            await pending;
            await transaction.CommitAsync(timeout.Token);

            // Assert
            discoveries.ShouldBeGreaterThanOrEqualTo(2);
            (await RowsAsync(session, "SELECT * FROM child", timeout.Token)).ShouldHaveSingleItem()
                .ShouldBe(new object?[] { 2, 1, 1 });
        }
        finally
        {
            SqlPlanExecutor.ReferenceIntentsDiscovered = null;
            grant.Set();
            try { await pending; } catch (DatabaseException) { /* Preserve the original test failure. */ }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rewrite: fresh child inserts inspect at most one retained publication marker (#1265)")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(128)]
    public async Task RewriteTableAsync_FreshForeignKeySnapshot_ShouldCheckConstantMarkers(int retirements)
    {
        // Arrange: retain unrelated owners behind a real snapshot pin.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("rewrite-marker-work", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await PrepareAsync(session, timeout.Token);
        await ExecAsync(session, "CREATE TABLE retired (id INT)", timeout.Token);
        await ExecAsync(session, "CREATE TABLE child (id INT REFERENCES t(id))", timeout.Token);
        await using var pin = await reader.BeginTransactionAsync(timeout.Token);
        for (int i = 0; i < retirements; i++) { await ExecAsync(session, "TRUNCATE retired", timeout.Token); }
        database.Catalog.GetPendingTableReclamations().Count.ShouldBe(retirements);
        int visits = 0;
        SqlCatalog.ReplacementMarkerExamined = () => visits++;
        try
        {
            // Act
            await ExecAsync(session, "INSERT INTO child VALUES (1)", timeout.Token);

            // Assert
            visits.ShouldBe(retirements == 0 ? 0 : 1);
            _output.WriteLine($"retained={retirements}; child INSERT replacement marker visits={visits}");
        }
        finally { SqlCatalog.ReplacementMarkerExamined = null; }
    }

#if !DEBUG
    [Fact(DisplayName = "Cohesion Test [Sql] - Rewrite: old snapshots allocate no per-retirement enumerators (#1265)")]
    public async Task SnapshotPredatesReplacement_UnrelatedRetirements_ShouldAllocateNothing()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("rewrite-marker-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await PrepareAsync(session, timeout.Token);
        await ExecAsync(session, "CREATE TABLE retired (id INT)", timeout.Token);
        await using var pin = await session.BeginTransactionAsync(timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        for (int i = 0; i < 128; i++) { await ExecAsync(writer, "TRUNCATE retired", timeout.Token); }
        var current = Table(database);
        var snapshot = pin.Context.Snapshot;
        for (int i = 0; i < 100; i++) { database.Catalog.SnapshotPredatesReplacement(current, snapshot).ShouldBeFalse(); }

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool predates = false;
        for (int i = 0; i < 1000; i++) { predates |= database.Catalog.SnapshotPredatesReplacement(current, snapshot); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        predates.ShouldBeFalse();
        allocated.ShouldBe(0);
        _output.WriteLine($"128 retained markers; old unrelated snapshot checks bytes/1000={allocated}");
    }
#endif
}
