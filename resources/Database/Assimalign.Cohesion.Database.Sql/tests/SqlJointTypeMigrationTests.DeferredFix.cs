using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    private sealed record NeighbourKey(int Id);
    private static CompiledSchemaKey NeighbourPrimaryKey() => SqlSchema.Create("typed", schema =>
        schema.Table<NeighbourKey>("key", table => table.Key(row => row.Id))).Compile().Tables.Single().PrimaryKey!;

    private static SqlCompiledSchema NeighbourDeclaration(string change = "none", bool planParent = false)
    {
        var parent = new CompiledSchemaTable("customers", "parent",
            [new("id", DatabaseType.Int32, false), new("note", change == "parentRewrite" ? DatabaseType.Int64 : DatabaseType.Int32, true)],
            NeighbourPrimaryKey(), change == "parentKey" ? [new("ux_base", ["id"], true), new("ux_id", ["id"], true)] :
                planParent && change == "index" ? [new("ux_base", ["id"], true), new("ix_note", ["note"], false)] : [new("ux_base", ["id"], true)], []);
        var child = new CompiledSchemaTable("orders", "child",
            [new("id", change == "childRewrite" ? DatabaseType.Int64 : DatabaseType.Int32, false), new("customer_id", DatabaseType.Int32, true)], NeighbourPrimaryKey(),
            !planParent && change is "index" or "childRewrite" or "repoint" or "dropFk" ? [new("ix_customer", ["customer_id"], false)] : [],
            change is "noFk" or "dropFk" ? [] : [new("fk", CompiledSchemaConstraintKind.Reference, ["customer_id"], change == "repoint" ? "alternate" : "customers", ["id"])]);
        return Compiled(parent, child, new("alternate", "alternate",
            [new("id", DatabaseType.Int32, false), new("note", DatabaseType.Int32, true)], NeighbourPrimaryKey(), [new("ux_alternate", ["id"], true)], []));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint deferred fix: stable neighbours admit reads, parent plain or joined non-key UPDATE and parent inserts during the job")]
    [InlineData("SELECT id FROM customers")]
    [InlineData("UPDATE customers SET note=8 WHERE id=1")]
    [InlineData("UPDATE customers SET note=s.note FROM alternate AS s WHERE customers.id=s.id")]
    [InlineData("INSERT INTO customers VALUES(2,8)")]
    public async Task ApplyAsync_DeferredFixStableNeighbour_ShouldCompleteDuringJob(string sql)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(NeighbourDeclaration(), guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT INTO customers VALUES(1,7)", cancellationToken: guard.Token);
        await session.ExecuteAsync("INSERT INTO orders VALUES(1,1)", cancellationToken: guard.Token);
        await session.ExecuteAsync("INSERT INTO alternate VALUES(1,8)", cancellationToken: guard.Token);
        database.SchemaProvisioner.DeferredFailed = failure => ready.TrySetException(failure);
        database.SchemaProvisioner.JointStage = phase =>
        {
            if (phase == "IntentDurable")
            { database.Catalog.PendingMigration!.Steps.ShouldContain(step => step.Kind == "Joint/KeepOwner" && step.Table == "orders"); }
            if (phase == "AfterTreeBuild") { ready.TrySetResult(); release.Wait(guard.Token); }
        };
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };

        // Act
        await database.SchemaProvisioner.ApplyAsync(NeighbourDeclaration("index"), guard.Token, deferDataScalingSteps: true);
        await ready.Task.WaitAsync(guard.Token);
        try
        {
            var statement = sql.StartsWith("SELECT", StringComparison.Ordinal)
                ? RowsAsync(session, sql, guard.Token) : ExecuteNeighbourAsync(session, sql, guard.Token);
            var first = await Task.WhenAny(statement, waiting.Task).WaitAsync(guard.Token);
            first.ShouldBeSameAs(statement);
            await statement;
            if (sql.StartsWith("UPDATE", StringComparison.Ordinal))
            { (await RowsAsync(session, "SELECT note FROM customers WHERE id=1", guard.Token)).Single()[0].ShouldBe(8); }
            waiting.Task.IsCompleted.ShouldBeFalse();
        }
        finally { release.Set(); await WaitDoneAsync(database, guard.Token); }
    }

    private static async Task<object?[][]> ExecuteNeighbourAsync(SqlDatabaseSession session, string sql, CancellationToken token)
    { await session.ExecuteAsync(sql, cancellationToken: token); return []; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint deferred fix: waiting FK probes refresh after publication and enforce the constraint")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApplyAsync_DeferredFixNeighbourProbe_ShouldWaitAndEnforcePublishedConstraint(bool childInsert, bool violation)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(NeighbourDeclaration(), guard.Token);
        await using var seed = await database.CreateSessionAsync(guard.Token);
        await seed.ExecuteAsync("INSERT INTO customers VALUES(1,7)", cancellationToken: guard.Token);
        await seed.ExecuteAsync(violation && !childInsert ? "INSERT INTO orders VALUES(1,1)" : "INSERT INTO orders VALUES(1,NULL)", cancellationToken: guard.Token);
        database.SchemaProvisioner.DeferredFailed = failure => ready.TrySetException(failure);
        database.SchemaProvisioner.JointStage = phase =>
        {
            if (phase == "Handoff" && childInsert)
            { database.SchemaProvisioner.Deferred!.JointPlan.Tables.Single().KeepsOwner.ShouldBeTrue(); }
            if (phase == "TreesDurable") { ready.TrySetResult(); release.Wait(guard.Token); }
        };
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };
        await database.SchemaProvisioner.ApplyAsync(NeighbourDeclaration("index", childInsert), guard.Token, deferDataScalingSteps: true);
        database.SchemaProvisioner.Deferred.ShouldNotBeNull();
        await ready.Task.WaitAsync(guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);

        // Act
        var write = writer.ExecuteAsync(childInsert ? $"INSERT INTO orders VALUES(2,{(violation ? 99 : 1)})" : "DELETE FROM customers WHERE id=1", cancellationToken: guard.Token).AsTask();
        try { await waiting.Task.WaitAsync(guard.Token); write.IsCompleted.ShouldBeFalse(); }
        finally { release.Set(); }
        if (violation)
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await write);
            failure.Diagnostics.ShouldContain(value => value.Code == "CS03039");
        }
        else { await write; }
        await WaitDoneAsync(database, guard.Token);

        // Assert
        database.Catalog.SchemaState!.ContentHash.ShouldBe(NeighbourDeclaration("index", childInsert).Hash);
        database.Provisioning.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint deferred fix: changed references, either rewritten end and changed parent keys reserve neighbour reads")]
    [InlineData("addFk")]
    [InlineData("dropFk")]
    [InlineData("repoint")]
    [InlineData("childRewrite")]
    [InlineData("parentRewrite")]
    [InlineData("parentKey")]
    public async Task ApplyAsync_DeferredFixUnsafeNeighbour_ShouldReserveReads(string change)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = NeighbourDeclaration(change == "addFk" ? "noFk" : "none");
        var target = NeighbourDeclaration(change == "addFk" ? "index" : change);
        await database.ApplySchemaAsync(source, guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("INSERT INTO customers VALUES(1,7)", cancellationToken: guard.Token);
        await session.ExecuteAsync("INSERT INTO alternate VALUES(1,7)", cancellationToken: guard.Token);
        await session.ExecuteAsync("INSERT INTO orders VALUES(1,1)", cancellationToken: guard.Token);
        database.SchemaProvisioner.JointStage = phase =>
        { if (phase == "TreesDurable") { ready.TrySetResult(); release.Wait(guard.Token); } };
        await database.SchemaProvisioner.ApplyAsync(target, guard.Token, deferDataScalingSteps: true);
        await ready.Task.WaitAsync(guard.Token);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };

        // Act
        var read = RowsAsync(session, change is "parentRewrite" or "parentKey" ? "SELECT id FROM orders" : "SELECT id FROM customers", guard.Token);
        try { await waiting.Task.WaitAsync(guard.Token); read.IsCompleted.ShouldBeFalse(); }
        finally { release.Set(); }
        await read;
        await WaitDoneAsync(database, guard.Token);

        // Assert
        database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint deferred fix: startup statements on new tables wait before binding for success or ordinary undefined-table failure")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task BuildAsync_DeferredFixNewTable_ShouldBindAfterReservation(bool transaction, bool read, bool fail)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).AddDatabase(DeferredDeclaration(false)).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var builder = Builder(storage);
        builder.AddWorker(engine =>
        {
            engine.BeforeProvisioning = database => database.SchemaProvisioner.JointStage = phase =>
            {
                if (phase != "TreesDurable") { return; }
                ready.TrySetResult(); release.Wait(guard.Token);
                if (fail) { throw new InvalidOperationException("injected new-table preparation failure"); }
            };
            return new RecordingWorker(engine);
        });
        await using var engine = await builder.AddDatabase(DeferredDeclaration(true)).BuildAsync(guard.Token);
        var current = await engine.OpenDatabaseAsync("typed", guard.Token);
        await using var session = await current.CreateSessionAsync(guard.Token);
        await using var live = transaction ? await session.BeginTransactionAsync(guard.Token) : null;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(current.Name);
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };

        // Act: startup seed immediately after Build, including case-insensitive names.
        var statement = read ? RowsAsync(session, "SELECT Id FROM DBO.CREATED", guard.Token) : ExecuteNeighbourAsync(session, "INSERT INTO DBO.CREATED VALUES(7)", guard.Token);
        try
        {
            await ready.Task.WaitAsync(guard.Token);
            var first = await Task.WhenAny(waiting.Task, statement).WaitAsync(guard.Token);
            first.ShouldBeSameAs(waiting.Task);
            statement.IsCompleted.ShouldBeFalse();
        }
        finally { release.Set(); }

        // Assert
        if (fail)
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await statement);
            failure.Diagnostics.ShouldContain(value => value.Code == "CS01014");
        }
        else { await statement; if (live is not null) { await live.CommitAsync(guard.Token); } }
        await WaitDoneAsync(current, guard.Token);
        current.Catalog.TryGetTable("dbo", "created", out _).ShouldBe(!fail);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint deferred fix: no-target discard has no report and successful Verify clears the observation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_DeferredFixDiscardObservation_ShouldHaveAnExit(bool verify)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var seed = await Builder(storage).AddDatabase(DeferredDeclaration(false)).BuildAsync(guard.Token))
        {
            var database = await seed.OpenDatabaseAsync("typed", guard.Token);
            var target = DeferredDeclaration(true).Compile();
            await database.Catalog.SavePendingMigrationAsync(new(target.Hash, target.CanonicalDocument,
                [new("Joint/AddIndex", "items", "ix", 1), new("Joint/KeepOwner", "items", "items", 2)], 2), guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var builder = Builder(storage);
        if (verify) { builder.AddDatabase("typed", db => { db.Schema(DeferredDeclaration(false)); db.Provisioning = SqlProvisioningMode.Verify; }); }

        // Act
        await using var engine = await builder.BuildAsync(guard.Token);
        var current = await engine.OpenDatabaseAsync("typed", guard.Token);

        // Assert
        current.Catalog.PendingMigration.ShouldBeNull();
        current.Catalog.SchemaState!.ContentHash.ShouldBe(DeferredDeclaration(false).Compile().Hash);
        current.Provisioning.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint deferred fix: an in-process reopen discards latched preparation and reports the unapplied declaration")]
    public async Task OpenAsync_DeferredFixLatchedPreparation_ShouldDiscardAndReport()
    {
        // Arrange: real files retain the durable intent across a same-engine reopen.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string path = Path.Combine(Path.GetTempPath(), "cohesion-a5-reopen", Guid.NewGuid().ToString("N"));
        try
        {
            var builder = SqlDatabaseEngine.CreateBuilder("latched-joint");
            builder.Options.RootPath = path;
            await using (var seed = await builder.AddDatabase(DeferredDeclaration(false)).BuildAsync(guard.Token))
            {
                var database = await seed.OpenDatabaseAsync("typed", guard.Token);
                await using var writer = await database.CreateSessionAsync(guard.Token);
                await writer.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
            }
            builder = SqlDatabaseEngine.CreateBuilder("latched-joint");
            builder.Options.RootPath = path;
            int latched = 0;
            SqlDatabase? preparing = null;
            builder.AddWorker(engine =>
            {
                engine.BeforeProvisioning = database =>
                {
                    preparing = database;
                    database.SchemaProvisioner.JointStage = phase =>
                    {
                        if (phase != "TreesDurable") { return; }
                        Interlocked.Increment(ref latched);
                        database.DataStorage.TakeOffline(StorageOfflineCause.PageWriteBackFailures, "injected preparation latch", new IOException("injected"));
                        throw new IOException("injected");
                    };
                };
                return new RecordingWorker(engine);
            });
            await using var engine = await builder.AddDatabase(DeferredDeclaration(true)).BuildAsync(guard.Token);
            var failed = preparing.ShouldNotBeNull();
            while (failed.SchemaProvisioner.Deferred is not null) { await Task.Delay(1, guard.Token); }
            latched.ShouldBe(1);
            failed.IsOffline.ShouldBeTrue();
            await failed.DisposeAsync();

            // Act
            var reopened = await engine.OpenDatabaseAsync("typed", guard.Token);

            // Assert
            latched.ShouldBe(1);
            reopened.IsOffline.ShouldBeFalse();
            reopened.Catalog.PendingMigration.ShouldBeNull();
            reopened.Catalog.SchemaState!.ContentHash.ShouldBe(DeferredDeclaration(false).Compile().Hash);
            reopened.Provisioning!.FailureCode.ShouldBe("CS03402");
            await using var reader = await reopened.CreateSessionAsync(guard.Token);
            (await RowsAsync(reader, "SELECT Id FROM items", guard.Token)).Single()[0].ShouldBe(1);
        }
        finally { if (Directory.Exists(path)) { Directory.Delete(path, true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint deferred fix: an unrendered layout change retains the job label without counting ownership metadata")]
    public async Task ApplyAsync_DeferredFixUnrenderedProgress_ShouldPublishAndRestartRowsWithJobLabel()
    {
        // Arrange: only audit has a rendered step. Items is copied from its complete
        // target description; its classification is not an executable step.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = Compiled(new("items", "items", [new("a", DatabaseType.Int32, false), new("b", DatabaseType.Int32, false)], new("pk", ["a"]), [], []),
            new("audit", "audit", [new("v", DatabaseType.Int32, false)], new("pk", ["v"]), [], []));
        var items = source.Tables.Single(table => table.Name == "items");
        var target = Compiled(new("items", "items", [items.Columns[1], items.Columns[0]], items.PrimaryKey, [], []),
            new("audit", "audit", [new("v", DatabaseType.Int64, false)], new("pk", ["v"]), [], []));
        await database.ApplySchemaAsync(source, guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1,2),(3,4)", cancellationToken: guard.Token);
        await writer.ExecuteAsync("INSERT INTO audit VALUES(7)", cancellationToken: guard.Token);
        int tables = 0;
        database.SchemaProvisioner.JointStage = phase =>
        {
            if (phase != "TablePrepared") { return; }
            var migration = database.SchemaProvisioner.Deferred!;
            var current = migration.JointPlan.Tables.Where(table => !table.KeepsOwner).ElementAt(tables++).Shape.Name;
            var first = migration.Intent.Steps[0];
            database.Provisioning!.Operation.ShouldBe(first.Kind);
            database.Provisioning.ObjectName.ShouldBe(first.Table);
            database.Provisioning.Position.ShouldBe(first.Position);
            database.Provisioning.StepCount.ShouldBe(1);
            database.Provisioning.RowsProcessed.ShouldBe(current == "items" ? 2L : 1L);
        };
        // Act
        await database.SchemaProvisioner.ApplyAsync(target, guard.Token, deferDataScalingSteps: true);
        await WaitDoneAsync(database, guard.Token);
        // Assert
        tables.ShouldBe(2);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
        var rows = await RowsAsync(writer, "SELECT b,a FROM items ORDER BY a", guard.Token);
        rows.Select(row => row[0]).ShouldBe(new object?[] { 2, 4 });
        rows.Select(row => row[1]).ShouldBe(new object?[] { 1, 3 });
    }
}
