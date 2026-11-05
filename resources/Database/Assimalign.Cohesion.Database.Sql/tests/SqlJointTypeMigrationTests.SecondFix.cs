using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Joint second fix: reopen without intent reports an unapplied declaration and clears a successful one")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_SecondFixNoIntentAfterLatch_ShouldReflectDeclaredSchema(bool fail)
    {
        // Arrange: the job finishes and clears kind 9 before an independent storage latch.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string path = Path.Combine(Path.GetTempPath(), "cohesion-a5-no-intent", Guid.NewGuid().ToString("N"));
        try
        {
            var builder = SqlDatabaseEngine.CreateBuilder("no-intent-joint");
            builder.Options.RootPath = path;
            await using (var seed = await builder.AddDatabase(DeferredDeclaration(false)).BuildAsync(guard.Token))
            {
                var database = await seed.OpenDatabaseAsync("typed", guard.Token);
                await using var writer = await database.CreateSessionAsync(guard.Token);
                await writer.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
            }
            builder = SqlDatabaseEngine.CreateBuilder("no-intent-joint");
            builder.Options.RootPath = path;
            builder.AddWorker(engine =>
            {
                engine.BeforeProvisioning = database => database.SchemaProvisioner.JointStage = phase =>
                { if (fail && phase == "TreesDurable") { throw new InvalidOperationException("injected job failure"); } };
                return new RecordingWorker(engine);
            });
            await using var engine = await builder.AddDatabase(DeferredDeclaration(true)).BuildAsync(guard.Token);
            var current = await engine.OpenDatabaseAsync("typed", guard.Token);
            await WaitDoneAsync(current, guard.Token);
            current.Catalog.PendingMigration.ShouldBeNull();
            if (fail) { current.Provisioning!.FailureCode.ShouldBe("CS03402"); }
            else { current.Provisioning.ShouldBeNull(); }
            current.DataStorage.TakeOffline(StorageOfflineCause.PageWriteBackFailures, "injected later latch", new IOException("injected"));
            await current.DisposeAsync();

            // Act
            var reopened = await engine.OpenDatabaseAsync("typed", guard.Token);

            // Assert: Hosting derives Degraded from this progress and Healthy from null.
            reopened.IsOffline.ShouldBeFalse();
            reopened.Catalog.PendingMigration.ShouldBeNull();
            reopened.Catalog.SchemaState!.ContentHash.ShouldBe(DeferredDeclaration(!fail).Compile().Hash);
            if (fail) { reopened.Provisioning!.FailureCode.ShouldBe("CS03402"); reopened.Provisioning.IsRunning.ShouldBeFalse(); }
            else { reopened.Provisioning.ShouldBeNull(); }
        }
        finally { if (Directory.Exists(path)) { Directory.Delete(path, true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint second fix: measured space selects both copy checkpoint thresholds")]
    public async Task ApplyAsync_SecondFixThresholdBranches_ShouldChargeSelectedBudget()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var storage = new CrashCaptureSqlStorageStrategy();
        var source = SqlSchema.Create("typed", schema => schema.Table<OtherRow>("items", table => table.Key(row => row.Id))).Compile();
        var target = ChangeItems(source, indexes: [new("ix", ["Id"], false)]);
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(source, guard.Token);
            await SeedShuffledIndexRowsAsync(database, 65536, guard.Token);
            database.Coordinator.Checkpoint(guard.Token);
            storage = storage.CaptureDurableImages();
        }
        var needs = new long[2];
        for (int mode = 0; mode < 2; mode++)
        {
            var image = storage.CaptureDurableImages();
            var builder = Builder(image);
            builder.Options.CheckpointJournalSize = 256L * 1024 * 1024;
            await using var engine = await builder.BuildAsync(guard.Token);
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            if (mode == 0) { image.VolumeCapacityBytes = image.AllocatedBytes + 192L * 1024 * 1024; }

            // Act
            await database.SchemaProvisioner.ApplyAsync(target, guard.Token, deferDataScalingSteps: true);
            await WaitDoneAsync(database, guard.Token);

            // Assert
            database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
            database.Provisioning.ShouldBeNull();
            database.SchemaProvisioner.JointCopyCheckpointThreshold.ShouldBe(mode == 0 ? 16L * 1024 * 1024 : 256L * 1024 * 1024);
            needs[mode] = database.SchemaProvisioner.JointSpaceNeed;
        }
        needs[1].ShouldBeGreaterThan(needs[0]);
        _output.WriteLine($"Threshold admission: 16 MiB N={needs[0]}; 256 MiB N={needs[1]}");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint second fix: a parent's plain and joined non-key UPDATE proceeds while session DDL holds the child outside any job")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SecondFixParentNonKeyOutsideJob_ShouldProceedDuringChildDdl(bool joined)
    {
        // Arrange: a real child ALTER TYPE has acquired X before its copier pauses.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = await Builder(new()).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var setup = await database.CreateSessionAsync(guard.Token);
        foreach (string sql in new[] { "CREATE TABLE parent(id INT PRIMARY KEY,note INT)",
            "CREATE TABLE child(id INT PRIMARY KEY,parent_id INT REFERENCES parent(id),note INT)",
            "INSERT INTO parent VALUES(1,7)", "INSERT INTO child VALUES(1,1,7)", "CREATE TABLE source(id INT,note INT)", "INSERT INTO source VALUES(1,8)" })
        { await setup.ExecuteAsync(sql, cancellationToken: guard.Token); }
        SqlPlanExecutor.RewriteStage = (phase, _) =>
        { if (phase == "Reserved") { ready.TrySetResult(); release.Wait(guard.Token); } };
        await using var ddlSession = await database.CreateSessionAsync(guard.Token);
        var ddl = Task.Run(async () => await ddlSession.ExecuteAsync("ALTER TABLE child ALTER COLUMN note TYPE BIGINT", cancellationToken: guard.Token), guard.Token);
        try
        {
            await ready.Task.WaitAsync(guard.Token);
            database.SchemaProvisioner.Deferred.ShouldBeNull();
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var listener = new LockWaitListener(database.Name);
            listener.Observed = value => { if (value.EventName == "LockWaitStart") { waiting.TrySetResult(); } };

            // Act
            var update = setup.ExecuteAsync(joined ? "UPDATE parent SET note=s.note FROM source AS s WHERE parent.id=s.id" : "UPDATE parent SET note=8 WHERE id=1", cancellationToken: guard.Token).AsTask();

            // Assert
            (await Task.WhenAny(update, waiting.Task).WaitAsync(guard.Token)).ShouldBeSameAs(update);
            (await update).AffectedCount.ShouldBe(1);
            waiting.Task.IsCompleted.ShouldBeFalse();
        }
        finally { release.Set(); SqlPlanExecutor.RewriteStage = null; await ddl; }
    }
}
