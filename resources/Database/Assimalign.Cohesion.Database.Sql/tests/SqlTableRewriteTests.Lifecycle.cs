using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlTableRewriteTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Rewrite: enqueue failure releases the new item's cancellation and admission ownership")]
    public async Task RewriteTableAsync_EnqueueFailure_ShouldReleaseAdmissionAndAllowRetry()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = SqlDatabaseEngine.Create("rewrite-enqueue-failure", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await PrepareAsync(session, timeout.Token);
        var worker = engine.Workers.OfType<SqlProvisioningWorker>().Single();
        var expected = new InvalidOperationException("Injected enqueue failure");
        worker.BeforeEnqueue = () => throw expected;
        try
        {
            // Act
            var failure = await Should.ThrowAsync<InvalidOperationException>(() => database.RewriteTableAsync(Table(database), cancellation.Token).AsTask());

            // Assert
            failure.ShouldBeSameAs(expected);
            worker.PendingRewriteCount.ShouldBe(0);
            cancellation.Cancel();
            await ExecAsync(session, "ALTER TABLE t ADD n INT", timeout.Token);
        }
        finally { worker.BeforeEnqueue = null; }
        await database.RewriteTableAsync(Table(database), timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rewrite: the public maintenance API resolves decoded delimited names")]
    [InlineData("t")]
    [InlineData("a.b")]
    [InlineData("a space")]
    [InlineData("a\"quote")]
    public async Task RewriteTableAsync_DecodedNames_ShouldPreserveCatalogIdentity(string name)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("rewrite-public", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        string identifier = '"' + name.Replace("\"", "\"\"") + '"';
        await ExecAsync(session, $"CREATE TABLE {identifier} (gone INT,id INT PRIMARY KEY)", timeout.Token);
        await ExecAsync(session, $"INSERT INTO {identifier} VALUES (99,1)", timeout.Token);
        await ExecAsync(session, $"ALTER TABLE {identifier} DROP COLUMN gone", timeout.Token);

        // Act
        await database.RewriteTableAsync("DBO", name.ToUpperInvariant(), timeout.Token);

        // Assert
        database.Catalog.Tables.Single().PhysicalColumnCount.ShouldBe(1);
        (await RowsAsync(session, $"SELECT id FROM {identifier} WHERE id=1", timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Rewrite: default and missing names are refused before maintenance admission")]
    public async Task RewriteTableAsync_InvalidNames_ShouldRetainStandardGuardsAndCodedMissingTable()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("rewrite-public-guards", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);

        // Act / Assert
        await Should.ThrowAsync<ArgumentException>(() => database.RewriteTableAsync(default(SqlObjectName), "t", timeout.Token).AsTask());
        await Should.ThrowAsync<ArgumentException>(() => database.RewriteTableAsync("dbo", default(SqlObjectName), timeout.Token).AsTask());
        var missing = await Should.ThrowAsync<DatabaseException>(() => database.RewriteTableAsync("dbo", "missing", timeout.Token).AsTask());
        missing.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS01051");
        SqlObjectName.IsValid("  ", out _).ShouldBeFalse();
        SqlObjectName.IsValid(" a ", out _).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rewrite: final rollback boundary failures are coded and preserve primary evidence")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RewriteTableAsync_FinalCleanupFailure_ShouldTranslateAndRetainBothCauses(bool primaryFailure)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("rewrite-cleanup", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await PrepareAsync(session, timeout.Token);
        var primary = new InvalidOperationException("Injected copy failure");
        var cleanup = new StorageIOException("Injected final cleanup failure");
        int cleaned = 0;
        database.SchemaProvisioner.RewriteRolledBack = () => { cleaned++; throw cleanup; };
        SqlPlanExecutor.RewriteStage = (stage, _) => { if (primaryFailure && stage == "Copied") { throw primary; } };
        try
        {
            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(() => database.RewriteTableAsync(Table(database), timeout.Token).AsTask());

            // Assert
            cleaned.ShouldBe(1);
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS06041");
            if (primaryFailure)
            {
                failure.InnerException.ShouldBeOfType<AggregateException>().InnerExceptions.ShouldBe(new Exception[] { primary, cleanup });
            }
            else { failure.InnerException.ShouldBeSameAs(cleanup); }
        }
        finally
        {
            database.SchemaProvisioner.RewriteRolledBack = null;
            SqlPlanExecutor.RewriteStage = null;
        }
        database.Coordinator.GetOpenContexts().ShouldBeEmpty();
        await ReclaimAsync(database, timeout.Token);
        await database.RewriteTableAsync(Table(database), timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Rewrite: compaction restores catalog capacity after repeated column drops")]
    public async Task RewriteTableAsync_AccumulatedDrops_ShouldRestoreAddDropCapacity()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("rewrite-ordinals", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await ExecAsync(session, "CREATE TABLE t (id INT)", timeout.Token);
        for (int cycle = 0; cycle < 1_500; cycle++)
        {
            await ExecAsync(session, "ALTER TABLE t ADD COLUMN gone INT", timeout.Token);
            await ExecAsync(session, "ALTER TABLE t DROP COLUMN gone", timeout.Token);
        }
        await ExecAsync(session, "ALTER TABLE t ADD COLUMN tail INT", timeout.Token);
        await ExecAsync(session, "INSERT INTO t VALUES (1,2)", timeout.Token);
        Table(database).PhysicalColumnCount.ShouldBe(1_502);

        // Act
        await database.RewriteTableAsync(Table(database), timeout.Token);

        // Assert
        Table(database).PhysicalColumnCount.ShouldBe(2);
        await ExecAsync(session, "ALTER TABLE t ADD COLUMN again INT", timeout.Token);
        Table(database).GetPhysicalOrdinal(2).ShouldBe(2);
        (await RowsAsync(session, "SELECT * FROM t", timeout.Token)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 2, null });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rewrite: canceling or disposing queued work finishes before the blocked worker returns")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RewriteTableAsync_CanceledQueue_ShouldReleaseAdmissionWithoutWorker(bool dispose)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("rewrite-queue", new());
        var first = await engine.CreateDatabaseAsync("first", timeout.Token);
        var second = await engine.CreateDatabaseAsync("second", timeout.Token);
        await using var firstSession = await first.CreateSessionAsync(timeout.Token);
        await using var secondSession = await second.CreateSessionAsync(timeout.Token);
        await PrepareAsync(firstSession, timeout.Token);
        await PrepareAsync(secondSession, timeout.Token);
        var worker = engine.Workers.OfType<SqlProvisioningWorker>().Single();
        SqlPlanExecutor.RewriteStage = (stage, _) =>
        {
            if (stage == "Reserved") { entered.Set(); release.Wait(timeout.Token); }
        };
        var active = first.RewriteTableAsync(Table(first), timeout.Token).AsTask();
        Task? queued = null;
        try
        {
            entered.Wait(timeout.Token);
            queued = second.RewriteTableAsync(Table(second), cancel.Token).AsTask();
            worker.PendingRewriteCount.ShouldBe(1);

            // Act
            if (dispose) { await second.DisposeAsync(); }
            else { cancel.Cancel(); }
            await Should.ThrowAsync<OperationCanceledException>(async () => await queued.WaitAsync(timeout.Token));

            // Assert: ordinary DDL proves the canceled database's global admission ended.
            active.IsCompleted.ShouldBeFalse();
            worker.PendingRewriteCount.ShouldBe(0);
            if (dispose) { second.IsClosed.ShouldBeTrue(); }
            else
            {
                await ExecAsync(secondSession, "ALTER TABLE t ADD n INT", timeout.Token);
                second.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
                for (int iteration = 0; iteration < 32; iteration++)
                {
                    using var repeatedCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    var repeated = second.RewriteTableAsync(Table(second), repeatedCancellation.Token).AsTask();
                    worker.PendingRewriteCount.ShouldBe(1);
                    repeatedCancellation.Cancel();
                    await Should.ThrowAsync<OperationCanceledException>(async () => await repeated);
                    worker.PendingRewriteCount.ShouldBe(0);
                }
            }
        }
        finally
        {
            SqlPlanExecutor.RewriteStage = null;
            release.Set();
            await active;
            if (queued is not null) { try { await queued; } catch (OperationCanceledException) { } }
        }
        if (dispose)
        {
            var reopened = await engine.OpenDatabaseAsync("second", timeout.Token);
            Table(reopened).DroppedColumnOrdinals.Count.ShouldBe(1);
        }
        else { await second.RewriteTableAsync(Table(second), timeout.Token); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rewrite: disposal cancels and joins an active copy before closing files")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_ActiveRewrite_ShouldJoinBeforeStorageClose(bool asynchronous)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("rewrite-dispose", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await PrepareAsync(session, timeout.Token);
        SqlPlanExecutor.RewriteStage = (stage, _) =>
        {
            if (stage == "Copied") { entered.Set(); release.Wait(timeout.Token); }
        };
        var rewrite = database.RewriteTableAsync(Table(database), timeout.Token).AsTask();
        Task? disposing = null;
        try
        {
            entered.Wait(timeout.Token);

            // Act
            disposing = DedicatedTestTask.Run(async () =>
            {
                if (asynchronous) { await database.DisposeAsync(); }
                else { database.Dispose(); }
            }, timeout.Token);
            while (!database.SchemaProvisioner.RewriteCancellationRequested) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
            disposing.IsCompleted.ShouldBeFalse();
            release.Set();
            await disposing.WaitAsync(timeout.Token);

            // Assert
            await Should.ThrowAsync<OperationCanceledException>(async () => await rewrite);
        }
        finally
        {
            SqlPlanExecutor.RewriteStage = null;
            release.Set();
            if (disposing is not null) { await disposing; }
            try { await rewrite; } catch (OperationCanceledException) { }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Rewrite: cancellation while waiting to publish releases the snapshot semaphore")]
    public async Task RewriteTableAsync_CanceledSnapshotGateWait_ShouldLeavePublicationUsable()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create("rewrite-gate", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await PrepareAsync(session, timeout.Token);
        SqlPlanExecutor.RewriteStage = (stage, _) => { if (stage == "Prepared") { prepared.TrySetResult(); } };
        await database.Catalog.TruncateSnapshotGate.WaitAsync(timeout.Token);
        var rewrite = database.RewriteTableAsync(Table(database), cancel.Token).AsTask();
        try
        {
            await prepared.Task.WaitAsync(timeout.Token);

            // Act
            cancel.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(async () => await rewrite.WaitAsync(timeout.Token));
        }
        finally
        {
            SqlPlanExecutor.RewriteStage = null;
            cancel.Cancel();
            database.Catalog.TruncateSnapshotGate.Release();
            try { await rewrite; } catch (OperationCanceledException) { }
        }

        // Assert
        await database.RewriteTableAsync(Table(database), timeout.Token);
        await ReclaimAsync(database, timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Rewrite: durable batches remain unreachable until complete publication")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RewriteTableAsync_MultipleBatches_ShouldPublishAllOrReclaimAbandonedRows(bool cancel)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = SqlDatabaseEngine.Create("rewrite-batches", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await PrepareAsync(session, timeout.Token);
        await ExecAsync(session, "INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(2, 191).Select(id => $"({id})")), timeout.Token);
        int batches = 0;
        ulong preparedOwner = 0;
        SqlPlanExecutor.RewriteStage = (stage, processed) =>
        {
            if (stage == "Reserved") { preparedOwner = checked((ulong)processed); }
            if (stage != "Copied") { return; }
            batches++;
            if (cancel && processed == 128) { cancellation.Cancel(); }
        };
        var previous = Table(database);
        try
        {
            // Act
            if (cancel) { await Should.ThrowAsync<OperationCanceledException>(() => database.RewriteTableAsync(previous, cancellation.Token).AsTask()); }
            else { await database.RewriteTableAsync(previous, timeout.Token); }
        }
        finally { SqlPlanExecutor.RewriteStage = null; }

        // Assert
        batches.ShouldBe(cancel ? 2 : 3);
        if (cancel) { Table(database).ShouldBeSameAs(previous); }
        else { Table(database).DroppedColumnOrdinals.ShouldBeEmpty(); }
        (await RowsAsync(session, "SELECT id FROM t ORDER BY id", timeout.Token)).Select(row => (int)row[0]!).ShouldBe(Enumerable.Range(1, 192));
        await ReclaimAsync(database, timeout.Token);
        preparedOwner.ShouldBeGreaterThan(previous.ObjectId);
        database.DataStorage.HasOwnerPages(cancel ? preparedOwner : previous.ObjectId).ShouldBeFalse();
        database.DataStorage.HasOwnerPages(Table(database).ObjectId).ShouldBeTrue();
        database.IndexManager.ExportRegistrations().ShouldAllBe(index => index.ObjectId == Table(database).ObjectId);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Rewrite: physical maintenance preserves compiled-schema verification and ownership")]
    public async Task RewriteTableAsync_SchemaOwnedTable_ShouldPreserveAppliedState()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("rewrite-schema", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "db", false, [],
            [new CompiledSchemaTable("t", "Tests.Row", [new CompiledSchemaColumn("id", DatabaseType.Int32, true)], null, [], [])], []);
        await database.ApplySchemaAsync(schema, timeout.Token);
        var previous = Table(database);

        // Act
        await database.RewriteTableAsync(previous, timeout.Token);

        // Assert
        Table(database).Owner.ShouldBe(DatabaseObjectOwner.Schema);
        Table(database).OwningSchema.ShouldBe(previous.OwningSchema);
        (await database.ApplySchemaAsync(schema, timeout.Token)).WasAlreadyApplied.ShouldBeTrue();
    }
}
