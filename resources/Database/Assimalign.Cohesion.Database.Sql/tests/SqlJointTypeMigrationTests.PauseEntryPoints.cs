using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Recovered catalog pause preserves first Build, inline Build and empty-table Build (#1283)")]
    [InlineData("FirstBuild")]
    [InlineData("InlineBuild")]
    [InlineData("EmptyBuild")]
    public async Task BuildAsync_RecoveredCatalogPaused_ShouldWaitAndPublish(string mode)
    {
        // Arrange: capture the catalog's uncheckpointed journal before closing its source.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var storage = new CrashCaptureSqlStorageStrategy();
        CrashCaptureSqlStorageStrategy crash;
        await using (var seed = await Builder(storage).BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            if (mode != "FirstBuild")
            {
                await database.ApplySchemaAsync(Source().Compile(), guard.Token);
                if (mode == "InlineBuild")
                {
                    await using var session = await database.CreateSessionAsync(guard.Token);
                    await session.ExecuteAsync("INSERT INTO items VALUES(1,1)", cancellationToken: guard.Token);
                }
            }
            for (int i = 0; i < 32; i++)
            { using var bracket = database.CatalogStorage.BeginTransaction(); bracket.Commit(); }
            database.CatalogStorage.JournalBytesSinceCheckpoint.ShouldBeGreaterThan(512);
            crash = storage.CaptureDurableImages();
        }
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SqlDatabase? observed = null;
        SqlCheckpointWorker? worker = null;
        var builder = Builder(crash);
        builder.Options.CheckpointJournalSize = 512;
        builder.Options.JournalSizeLimit = 1024;
        builder.Options.CheckpointInterval = TimeSpan.FromHours(1);
        builder.AddWorker(owner =>
        {
            worker = owner.Workers.OfType<SqlCheckpointWorker>().Single();
            worker.BeforeCheckpoint = _ => { entered.TrySetResult(); release.Wait(guard.Token); };
            owner.BeforeProvisioning = database => observed = database;
            return new RecordingWorker(owner);
        });
        builder.AddDatabase("typed", database => { database.Schema(Target()); database.RunMigrationsInline = mode != "EmptyBuild"; });
        using var recorder = new EventSourceRecorder(EventSource.GetSources().Single(s =>
            s.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);
        var building = Task.Run(async () => await builder.BuildAsync(guard.Token), guard.Token);
        try
        {
            // Act: first admission to the recovered catalog must yield its schema semaphore.
            await Task.WhenAny(entered.Task, building).WaitAsync(guard.Token);
            entered.Task.IsCompleted.ShouldBeTrue("Build must reach the held checkpoint");
            var parked = recorder.WaitForAsync(e => e.EventId == 25 && Equals(e.Payload![1], "SqlMigration"), guard.Token);
            await Task.WhenAny(parked, building).WaitAsync(guard.Token);
            parked.IsCompleted.ShouldBeTrue("Build must wait rather than expose an admission refusal");
            building.IsCompleted.ShouldBeFalse();
            await Task.Delay(250, guard.Token);
            release.Set();
            await using var engine = await building.WaitAsync(guard.Token);
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            // Assert
            database.Catalog.SchemaState!.ContentHash.ShouldBe(Target().Compile().Hash);
            database.Catalog.PendingMigration.ShouldBeNull(); database.IsOffline.ShouldBeFalse();
            _output.WriteLine($"entry={mode}, outcome=Applied, pauses={recorder.Events.Count(e => e.EventId == 23)}, resumes={recorder.Events.Count(e => e.EventId == 24)}, waits={recorder.Events.Count(e => e.EventId == 25)}");
        }
        finally
        {
            release.Set(); if (worker is not null) { worker.BeforeCheckpoint = null; }
            if (observed is not null) { TransactionCheckpointInterlock.ForStorage(observed.CatalogStorage).JournalSizeLimit = 0; }
            var completed = await building.WaitAsync(guard.Token);
            await completed.DisposeAsync();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Migration pause honors caller cancellation, lock timeout and stop with a coded outcome (#1283)")]
    [InlineData("Cancel")]
    [InlineData("Timeout")]
    [InlineData("Stop")]
    public async Task ApplyAsync_PauseInterrupted_ShouldCodeFailureAndDiscardIntent(string ending)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        var builder = Builder(await SeedAsync(guard.Token));
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromHours(1);
        if (ending == "Timeout") { builder.Options.LockTimeout = TimeSpan.FromMilliseconds(200); }
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.OpenDatabaseAsync("typed", guard.Token);
        var worker = engine.Workers.OfType<SqlCheckpointWorker>().Single();
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        using var release = new ManualResetEventSlim();
        int injected = 0;
        database.SchemaProvisioner.JointStage = point =>
        {
            if (point != "TreesDurable" || Interlocked.Exchange(ref injected, 1) != 0) { return; }
            worker.BeforeCheckpoint = _ => release.Wait(guard.Token);
            gate.JournalSizeLimit = 2; gate.ObserveAdmissionPause().ShouldBeTrue();
        };
        Exception? stoppedFailure = null;
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        database.SchemaProvisioner.DeferredFailed = failure => { stoppedFailure = failure; failed.TrySetResult(); };
        using var recorder = new EventSourceRecorder(EventSource.GetSources().Single(s =>
            s.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);
        var applying = Task.Run(async () => await database.SchemaProvisioner.ApplyAsync(Target().Compile(), caller.Token, ending == "Stop"), guard.Token);
        try
        {
            var parked = recorder.WaitForAsync(e => e.EventId == 25 && Equals(e.Payload![1], "SqlMigration"), guard.Token);
            await Task.WhenAny(parked, ending == "Stop" ? failed.Task : applying).WaitAsync(guard.Token);
            parked.IsCompleted.ShouldBeTrue("the migration must park before interruption");
            // Act
            if (ending == "Cancel") { caller.Cancel(); }
            Exception failure;
            if (ending == "Stop")
            {
                await applying.WaitAsync(guard.Token);
                await database.SchemaProvisioner.Deferred.ShouldNotBeNull().StopAsync().AsTask().WaitAsync(guard.Token);
                failure = stoppedFailure.ShouldNotBeNull();
            }
            else { failure = await Should.ThrowAsync<SqlTimeoutException>(() => applying.WaitAsync(guard.Token)); }
            // Assert
            failure.ShouldBeOfType<SqlTimeoutException>().Diagnostics.ShouldContain(d => d.Code == "CK04014");
            database.Catalog.PendingMigration.ShouldBeNull();
            database.Catalog.SchemaState!.ContentHash.ShouldBe(Source().Compile().Hash);
            database.IsOffline.ShouldBeFalse();
        }
        finally
        {
            release.Set(); worker.BeforeCheckpoint = null; gate.JournalSizeLimit = 0;
            try { await applying.WaitAsync(guard.Token); } catch (SqlTimeoutException) { }
        }
    }
}
