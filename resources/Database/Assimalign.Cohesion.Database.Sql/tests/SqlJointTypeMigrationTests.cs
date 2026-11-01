using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed partial class SqlJointTypeMigrationTests
{
    private readonly ITestOutputHelper _output;

    public SqlJointTypeMigrationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: late cancellation and observer OOM retain the committed new schema")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplySchemaAsync_AfterPublicationFault_ShouldKeepCommittedSuccess(bool oom)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = await Builder(await SeedAsync(timeout.Token)).BuildAsync(timeout.Token);
        var database = await engine.OpenDatabaseAsync("typed", timeout.Token);
        database.SchemaProvisioner.JointStage = phase =>
        {
            if (phase != "Published") { return; }
            if (oom) { throw new OutOfMemoryException("injected after publication"); }
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };

        // Act
        await database.ApplySchemaAsync(Target().Compile(), cancellation.Token);

        // Assert
        database.Catalog.SchemaState!.ContentHash.ShouldBe(Target().Compile().Hash);
        database.Catalog.PendingMigration.ShouldBeNull();
        database.Provisioning.ShouldBeNull();
        database.SchemaProvisioner.JointMetrics!.SelfCommits.ShouldBe(1);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        (await RowsAsync(reader, "SELECT Value FROM items WHERE Id=1", timeout.Token)).Single()[0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: empty Build, forced inline and Apply use one publication")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ApplySchemaAsync_InlinePaths_ShouldPublishOnce(int mode)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(timeout.Token, mode == 0 ? 0 : 65);
        var builder = Builder(storage);
        if (mode != 2) { builder.AddDatabase("typed", db => { db.Schema(Target()); db.RunMigrationsInline = mode == 1; }); }
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.OpenDatabaseAsync("typed", timeout.Token);
        ulong generation = database.Catalog.DdlGeneration;
        int published = 0;
        database.SchemaProvisioner.JointStage = stage => { if (stage == "Published") { published++; } };

        // Act
        if (mode == 2) { await database.ApplySchemaAsync(Target().Compile(), timeout.Token); }

        // Assert
        database.Catalog.PendingMigration.ShouldBeNull();
        database.SchemaProvisioner.Deferred.ShouldBeNull();
        database.Catalog.SchemaState!.ContentHash.ShouldBe(Target().Compile().Hash);
        Table(database).FindColumn("Value")!.Type.Type.ShouldBe(DatabaseType.Int64);
        if (mode == 2) { published.ShouldBe(1); database.Catalog.DdlGeneration.ShouldBe(generation + 1); }
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT INTO items(Id) VALUES(1000)", cancellationToken: timeout.Token);
        (await RowsAsync(session, "SELECT Value,Added FROM items WHERE Id=1000", timeout.Token)).Single().ShouldBe(new object?[] { 7L, 3 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: every pre-publication boundary preserves the old schema and permits retry")]
    [InlineData("IntentDurable")]
    [InlineData("LocksReserved")]
    [InlineData("OwnersDurable")]
    [InlineData("TreesDurable")]
    [InlineData("TablePrepared")]
    [InlineData("Validated")]
    [InlineData("Publishing")]
    public async Task ApplySchemaAsync_PhaseFailure_ShouldPreserveOldAndRetry(string phase)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = await SeedAsync(timeout.Token);
        await using var engine = await Builder(storage).BuildAsync(timeout.Token);
        var database = await engine.OpenDatabaseAsync("typed", timeout.Token);
        var old = Table(database);
        ulong generation = database.Catalog.DdlGeneration;
        database.SchemaProvisioner.JointStage = stage => { if (stage == phase) { throw new System.IO.IOException(phase); } };

        // Act
        await Should.ThrowAsync<Exception>(async () => await database.ApplySchemaAsync(Target().Compile(), timeout.Token));

        // Assert
        database.OfflineError.ShouldBeNull();
        Table(database).ShouldBeSameAs(old);
        database.Catalog.DdlGeneration.ShouldBe(generation);
        database.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
        database.Catalog.PendingMigration.ShouldBeNull();
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES(1000,9)", cancellationToken: timeout.Token);
        database.SchemaProvisioner.JointStage = null;
        await database.ApplySchemaAsync(Target().Compile(), timeout.Token);
        (await RowsAsync(session, "SELECT Value FROM items WHERE Id=1000", timeout.Token)).Single()[0].ShouldBe(9L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: cancellation and OOM discard prepared work without compensation")]
    [InlineData("OwnersDurable", false)]
    [InlineData("IntentDurable", false)]
    [InlineData("LocksReserved", false)]
    [InlineData("TreesDurable", false)]
    [InlineData("TablePrepared", false)]
    [InlineData("Validated", false)]
    [InlineData("Publishing", false)]
    [InlineData("OwnersDurable", true)]
    [InlineData("IntentDurable", true)]
    [InlineData("LocksReserved", true)]
    [InlineData("TreesDurable", true)]
    [InlineData("TablePrepared", true)]
    [InlineData("Validated", true)]
    [InlineData("Publishing", true)]
    public async Task ApplySchemaAsync_InterruptedPhase_ShouldKeepTouchedTablesUsable(string phase, bool oom)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = await Builder(await SeedAsync(timeout.Token)).BuildAsync(timeout.Token);
        var database = await engine.OpenDatabaseAsync("typed", timeout.Token);
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage != phase) { return; }
            if (oom) { throw new OutOfMemoryException("injected"); }
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };

        // Act
        await Should.ThrowAsync<Exception>(async () => await database.ApplySchemaAsync(Target().Compile(), cancellation.Token));

        // Assert
        database.OfflineError.ShouldBeNull();
        database.Provisioning!.FailureCode.ShouldBe("CS03402");
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT INTO items VALUES(1000,9)", cancellationToken: timeout.Token);
        database.SchemaProvisioner.JointStage = null;
        await database.ApplySchemaAsync(Target().Compile(), timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint TYPE: unpublished additions, old snapshots, writer retries and unrelated traffic")]
    public async Task BuildAsync_DeferredMigration_ShouldPublishAtomicallyAndExerciseAdmissionRace()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Builder(await SeedAsync(timeout.Token));
        builder.AddWorker(owner =>
        {
            owner.BeforeProvisioning = database => database.SchemaProvisioner.JointStage = stage =>
            { if (stage == "Validated") { entered.TrySetResult(); release.Wait(timeout.Token); } };
            return new RecordingWorker(owner);
        });
        await using var engine = await builder.AddDatabase(Target()).BuildAsync(timeout.Token);
        var database = await engine.OpenDatabaseAsync("typed", timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var previous = Table(database);
        await using var old = await database.CreateSessionAsync(timeout.Token);
        await using var snapshot = await old.BeginTransactionAsync(timeout.Token);
        int waits = 0, writes = 0;
        using var listener = new LockWaitListener(database.Name);
        TaskCompletionSource? waiting = null;
        listener.Observed = value => { if (value.EventName == "LockWaitStart") { Interlocked.Increment(ref waits); waiting?.TrySetResult(); } };
        try
        {
            // Act: run for a minimum interval and until counters prove repeated admission.
            var elapsed = Stopwatch.StartNew();
            await using var unrelated = await database.CreateSessionAsync(timeout.Token);
            do
            {
                Table(database).FindColumn("Added").ShouldBeNull();
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                await using var reader = await database.CreateSessionAsync(timeout.Token);
                waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var blocked = reader.ExecuteAsync("SELECT Value FROM items", cancellationToken: cancel.Token).AsTask();
                await waiting.Task.WaitAsync(timeout.Token);
                await unrelated.ExecuteAsync("INSERT INTO other VALUES(1)", cancellationToken: timeout.Token);
                writes++;
                cancel.Cancel();
                await Should.ThrowAsync<OperationCanceledException>(async () => await blocked);
            } while (elapsed.Elapsed < TimeSpan.FromMilliseconds(150) || writes < 8 || Volatile.Read(ref waits) < 8);
            release.Set();
            await WaitDoneAsync(database, timeout.Token);

            // Assert
            waits.ShouldBeGreaterThanOrEqualTo(8);
            writes.ShouldBeGreaterThanOrEqualTo(8);
            Table(database).ObjectId.ShouldNotBe(previous.ObjectId);
            (await RowsAsync(old, "SELECT Value FROM items WHERE Id=1", timeout.Token)).Single()[0].ShouldBe(1);
            var stale = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () =>
                await old.ExecuteAsync("UPDATE items SET Value=8 WHERE Id=1", cancellationToken: timeout.Token));
            stale.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS04006");
        }
        finally { release.Set(); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint TYPE: Build and recovery decode zero data rows regardless of table size")]
    [InlineData(1)]
    [InlineData(1024)]
    public async Task BuildAsync_TableSize_ShouldDecodeNoRowsBeforeHandoff(int count)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var storage = await SeedAsync(timeout.Token, count);
        long decoded = 0;
        var builder = Builder(storage);
        builder.AddWorker(owner =>
        {
            owner.BeforeProvisioning = database => database.SchemaProvisioner.BeforeDeferredStep = (_, token) => release.Wait(token);
            return new RecordingWorker(owner);
        });
        SqlPlanExecutor.RowDecoded = _ => Interlocked.Increment(ref decoded);
        try
        {
            await using (var baseline = await Builder(storage.CaptureDurableImages()).BuildAsync(timeout.Token))
            {
                await baseline.OpenDatabaseAsync("typed", timeout.Token);
                Interlocked.Read(ref decoded).ShouldBe(0);
            }
            // Act
            await using var engine = await builder.AddDatabase(Target()).BuildAsync(timeout.Token);
            var database = await engine.OpenDatabaseAsync("typed", timeout.Token);

            // Assert
            Interlocked.Read(ref decoded).ShouldBe(0);
            await using (var rollback = await Builder(storage.CaptureDurableImages()).AddDatabase(Source()).BuildAsync(timeout.Token))
            {
                var old = await rollback.OpenDatabaseAsync("typed", timeout.Token);
                old.Catalog.PendingMigration.ShouldBeNull();
                Interlocked.Read(ref decoded).ShouldBe(0);
            }
            _output.WriteLine($"Build/open decoded rows: {count} stored => baseline=0, joint=0, rollback=0.");
            release.Set();
            await WaitDoneAsync(database, timeout.Token);
        }
        finally { release.Set(); SqlPlanExecutor.RowDecoded = null; }
    }

    private static SqlSchema Source() => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        schema.Table<OldRow>("items", table => { table.Key(row => row.Id); table.Default(row => row.Value, 7); table.Index(row => row.Value); });
        schema.Table<OtherRow>("other", table => table.Column(row => row.Id));
    });

    private static SqlSchema Target() => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        schema.Table<NewRow>("items", table => { table.Key(row => row.Id); table.Default(row => row.Value, 7L); table.Index(row => row.Value); table.Default(row => row.Added, 3); });
        schema.Table<OtherRow>("other", table => table.Column(row => row.Id));
    });

    private static SqlDatabaseEngineBuilder Builder(CrashCaptureSqlStorageStrategy storage)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("joint-migrations");
        builder.Options.StorageStrategy = storage;
        builder.Options.Durability = storage.Durability;
        return builder;
    }

    private static async Task<CrashCaptureSqlStorageStrategy> SeedAsync(CancellationToken token, int count = 65)
    {
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).AddDatabase(Source()).BuildAsync(token);
        var database = await engine.OpenDatabaseAsync("typed", token);
        await using var session = await database.CreateSessionAsync(token);
        if (count > 0)
        { await session.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', Enumerable.Range(1, count).Select(id => $"({id},{id})")), cancellationToken: token); }
        await session.ExecuteAsync("CREATE VIEW ids AS SELECT Id FROM items", cancellationToken: token);
        return storage.CaptureDurableImages();
    }

    private static SqlCatalogTable Table(SqlDatabase database)
    {
        database.Catalog.TryGetTable("dbo", "items", out var table).ShouldBeTrue();
        return table;
    }

    private static async Task WaitDoneAsync(SqlDatabase database, CancellationToken token)
    {
        while (database.SchemaProvisioner.Deferred is not null) { await Task.Delay(1, token); }
        database.OfflineError.ShouldBeNull();
    }

    private static async Task<object?[][]> RowsAsync(SqlDatabaseSession session, string sql, CancellationToken token)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: token);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(token)) { rows.Add(Enumerable.Range(0, result.Columns.Count).Select(row.GetValue).ToArray()); }
        return rows.ToArray();
    }

    private sealed record OldRow(int Id, int Value);
    private sealed record NewRow(int Id, long Value, int Added);
    private sealed record OtherRow(int Id);
}
