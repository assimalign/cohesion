using System;
using System.Diagnostics.Tracing;
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
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Tests;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Migration retains 32768 copied rows through a 250 ms checkpoint pause at every stage (#1283)")]
    [InlineData("None", false)]
    [InlineData("Reservation", false)]
    [InlineData("Copy", false)]
    [InlineData("IndexBuild", false)]
    [InlineData("Validation", false)]
    [InlineData("Publication", false)]
    [InlineData("Cleanup", false)]
    [InlineData("None", true)]
    [InlineData("Reservation", true)]
    [InlineData("Copy", true)]
    [InlineData("IndexBuild", true)]
    [InlineData("Validation", true)]
    [InlineData("Publication", true)]
    [InlineData("Cleanup", true)]
    public async Task ApplyAsync_MomentaryPauseAtEachStage_ShouldRetainPreparedWork(string stage, bool deferred)
    {
        // Arrange: one rewritten heap, one kept heap with a new tree, four unrelated writers.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var builder = Builder(new CrashCaptureSqlStorageStrategy());
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromHours(1);
        builder.Options.MaintenanceInterval = TimeSpan.FromHours(1);
        if (deferred) { builder.Options.LockTimeout = TimeSpan.FromMilliseconds(100); }
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        var source = PauseProbeSchema(false);
        var target = PauseProbeSchema(true);
        await database.ApplySchemaAsync(source, guard.Token);
        SeedRaw(database, [1], 32768);
        await using var setup = await database.CreateSessionAsync(guard.Token);
        database.Catalog.TryGetTable("dbo", "indexed", out var indexed).ShouldBeTrue();
        byte[] indexedRecord = SqlRowCodec.Encode(indexed, [1], TransactionSequence.None);
        for (int first = 0; first < 32768; first += 64)
        {
            using var bracket = database.DataStorage.BeginTransaction();
            for (int row = 0; row < 64; row++) { database.DataStorage.InsertRow(bracket, indexed.ObjectId, indexedRecord); }
            bracket.Commit();
        }
        database.Coordinator.Checkpoint(guard.Token);
        var worker = engine.Workers.OfType<SqlCheckpointWorker>().Single();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        bool catalogPause = stage is "Publication" or "Reservation";
        var gate = TransactionCheckpointInterlock.ForStorage(catalogPause ? database.CatalogStorage : database.DataStorage);
        using var recorder = new EventSourceRecorder(EventSource.GetSources().Single(s =>
            s.Name == "Assimalign.Cohesion.Database.Transactions"), EventLevel.Verbose);
        int triggered = 0, copyBrackets = 0, indexBrackets = 0;
        long copied = 0, built = 0;
        bool buildingIndex = false;
        void Pause(string point)
        {
            if (stage != point || Interlocked.CompareExchange(ref triggered, 1, 0) != 0) { return; }
            worker.BeforeCheckpoint = _ =>
            {
                entered.TrySetResult(); release.Wait(guard.Token);
                // Preserve this episode until its checkpoint resumes it, then avoid
                // creating additional episodes during the remainder of the copy.
                gate.JournalSizeLimit = long.MaxValue;
            };
            // A committed empty bracket also covers the cleanup case after a copy checkpoint.
            var storage = catalogPause ? database.CatalogStorage : database.DataStorage;
            using (var bracket = storage.BeginTransaction("PauseProbe")) { bracket.Commit(); }
            gate.JournalSizeLimit = 2;
            gate.ObserveAdmissionPause().ShouldBeTrue();
        }
        SqlPlanExecutor.RewriteStage = (point, rows) =>
        {
            if (point != "Copied") { return; }
            copied = rows; copyBrackets++;
            if (rows == 64) { Pause("Copy"); }
        };
        database.SchemaProvisioner.JointStage = point =>
        {
            if (point == "LocksReserved") { Pause("Reservation"); }
            if (point == "BeforeTreeBuild") { buildingIndex = true; }
            if (point == "AfterTreeBuild") { buildingIndex = false; }
            if (point == "Validating") { Pause("Validation"); }
            if (point == "Publishing") { Pause("Publication"); }
            if (point == "Cleanup") { Pause("Cleanup"); }
        };
        database.SchemaProvisioner.DeferredProgress = (rows, _) =>
        {
            if (!buildingIndex) { return; }
            if (rows == 0) { built.ShouldBe(0, "a pause must not restart index construction"); }
            else if (rows != built) { rows.ShouldBe(built + 64); built = rows; indexBrackets++; }
            if (rows == 64) { Pause("IndexBuild"); }
        };
        var migration = Task.Run(async () =>
        {
            await database.SchemaProvisioner.ApplyAsync(target, guard.Token, deferred);
            await WaitDoneAsync(database, guard.Token);
        }, guard.Token);
        var writers = new Task[4];
        try
        {
            // Act: counters prove that the same job is parked outside its retained gates.
            if (stage != "None")
            {
                await Task.WhenAny(entered.Task, migration).WaitAsync(guard.Token);
                entered.Task.IsCompleted.ShouldBeTrue("the stage must reach the held checkpoint");
                var parked = recorder.WaitForAsync(e => e.EventId == 25 && Equals(e.Payload![1], "SqlMigration"), guard.Token);
                await Task.WhenAny(parked, migration).WaitAsync(guard.Token);
                parked.IsCompleted.ShouldBeTrue("the prepared job must wait rather than discard its work");
                migration.IsCompleted.ShouldBeFalse();
                if (stage != "Cleanup") { database.Catalog.PendingMigration.ShouldNotBeNull(); }
                await Should.ThrowAsync<DatabaseObjectLockedException>(() => database.VerifySchemaAsync(target, guard.Token).AsTask());
                // BEGIN takes the snapshot gate. This completes even at refused publication.
                await setup.ExecuteAsync("BEGIN", cancellationToken: guard.Token);
                await setup.ExecuteAsync("ROLLBACK", cancellationToken: guard.Token);
                for (int writer = 0; writer < writers.Length; writer++)
                {
                    int id = writer;
                    writers[writer] = Task.Run(async () =>
                    {
                        await using var session = await database.CreateSessionAsync(guard.Token);
                        await session.ExecuteAsync($"INSERT INTO writer{id} VALUES(1)", cancellationToken: guard.Token);
                    }, guard.Token);
                }
                if (!catalogPause)
                {
                    long migrationOwner = unchecked((long)database.SchemaProvisioner.Deferred!.Locks.Sequence.Value);
                    while (recorder.Events.Count(e => e.EventId == 25 && Equals(e.Payload![1], "Statement") &&
                        !Equals(e.Payload[2], migrationOwner)) < 4) { await Task.Delay(1, guard.Token); }
                }
                // The held interval is probe input; no elapsed-time assertion decides success.
                await Task.Delay(250, guard.Token);
                release.Set();
                await Task.WhenAll(writers).WaitAsync(guard.Token);
            }
            await migration.WaitAsync(guard.Token);

            // Assert: exactly 512 committed copy chunks, with the baseline's schema and values.
            copied.ShouldBe(32768); copyBrackets.ShouldBe(512); built.ShouldBe(32768); indexBrackets.ShouldBe(512);
            database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
            database.Catalog.PendingMigration.ShouldBeNull();
            database.Provisioning.ShouldBeNull(); database.IsOffline.ShouldBeFalse();
            (await RowsAsync(setup, "SELECT COUNT(*), SUM(Value) FROM items", guard.Token)).Single()
                .ShouldBe(new object?[] { 32768L, 32768L });
            (await RowsAsync(setup, "SELECT COUNT(*) FROM indexed WHERE Value=1", guard.Token)).Single()[0].ShouldBe(32768L);
            if (stage != "None")
            {
                triggered.ShouldBe(1);
                recorder.Events.ShouldContain(e => e.EventId == 24 && Equals(e.Payload![1], "CheckpointCompleted"));
                recorder.Events.Count(e => e.EventId == 23).ShouldBe(1);
                recorder.Events.Count(e => e.EventId == 24).ShouldBe(1);
            }
            _output.WriteLine($"stage={stage}, deferred={deferred}, copied={copied}, chunks={copyBrackets}, indexed={built}, indexChunks={indexBrackets}, outcome=Applied, pauses={recorder.Events.Count(e => e.EventId == 23)}, resumes={recorder.Events.Count(e => e.EventId == 24)}, waits={recorder.Events.Count(e => e.EventId == 25)}");
        }
        finally
        {
            release.Set(); worker.BeforeCheckpoint = null; gate.JournalSizeLimit = 0;
            SqlPlanExecutor.RewriteStage = null; database.SchemaProvisioner.JointStage = null;
            database.SchemaProvisioner.DeferredProgress = null;
            await migration.WaitAsync(guard.Token);
            foreach (var writing in writers) { if (writing is not null) { await writing.WaitAsync(guard.Token); } }
        }
    }

    private static SqlCompiledSchema PauseProbeSchema(bool target)
        => Compiled(new[]
        {
            new CompiledSchemaTable("items", "items", [new("Value", target ? DatabaseType.Int64 : DatabaseType.Int32, false)], null, [], []),
            new CompiledSchemaTable("indexed", "indexed", [new("Value", DatabaseType.Int32, false)], null,
                target ? [new("ix_probe", ["Value"], false)] : [], []),
        }.Concat(Enumerable.Range(0, 4).Select(id => new CompiledSchemaTable($"writer{id}", $"writer{id}",
            [new("Value", DatabaseType.Int32, false)], null, [], []))).ToArray());

    [Fact(DisplayName = "Cohesion Test [Sql] - Migration waiting at admission ends offline when the owned failure window expires (#1283)")]
    public async Task ApplyAsync_PauseFailureWindowExpires_ShouldEndOfflineAndRecoverOldIntent()
    {
        // Arrange: remain below J while paused, so only the failure-window policy decides offline.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var clock = new ManualTimeProvider();
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.TimeProvider = clock;
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromHours(1);
        builder.Options.MaintenanceInterval = TimeSpan.FromHours(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(PauseProbeSchema(false), guard.Token);
        SeedRaw(database, [1], 32768); database.Coordinator.Checkpoint(guard.Token);
        var gate = TransactionCheckpointInterlock.ForStorage(database.DataStorage);
        var worker = engine.Workers.OfType<SqlCheckpointWorker>().Single();
        Exception? outcome = null;
        database.SchemaProvisioner.DeferredFailed = failure => outcome = failure;
        int injected = 0;
        SqlPlanExecutor.RewriteStage = (point, count) =>
        {
            if (point != "Copied" || count != 64 || Interlocked.Exchange(ref injected, 1) != 0) { return; }
            worker.BeforeCheckpoint = _ => throw new IOException("checkpoint failure window probe");
            using (var bracket = database.DataStorage.BeginTransaction())
            {
                for (int page = 0; page < 16; page++)
                { using var allocated = database.DataStorage.AllocatePageForWrite(bracket, PageType.Data); allocated.Page.AsBodySpan().Fill(1); }
                bracket.Commit();
            }
            gate.JournalSizeLimit = 262144;
            gate.ObserveAdmissionPause().ShouldBeTrue();
        };
        try
        {
            // Act: the job is still reserved after three real failures inside the window.
            await database.SchemaProvisioner.ApplyAsync(PauseProbeSchema(true), guard.Token, true);
            while (worker.FailureCount < 3) { await Task.Delay(1, guard.Token); }
            database.IsOffline.ShouldBeFalse(); gate.IsAdmissionPaused.ShouldBeTrue();
            database.SchemaProvisioner.Deferred.ShouldNotBeNull();
            database.DataStorage.JournalLength.ShouldBeLessThan(262144);
            clock.Advance(DatabaseEngine.DefaultWorkerFailureWindow);
            while (!database.IsOffline || database.SchemaProvisioner.Deferred is not null) { await Task.Delay(1, guard.Token); }

            // Assert: offline retains its existing cause and recovery handles the durable intent.
            database.DataStorage.OfflineError!.Cause.ShouldBe(StorageOfflineCause.CheckpointFailures);
            outcome.ShouldBeOfType<SqlSchemaMigrationException>().Diagnostics.ShouldContain(d => d.Code == "CS03402");
            worker.BeforeCheckpoint = null; SqlPlanExecutor.RewriteStage = null;
            await using var recovery = await Builder(storage.CaptureDurableImages()).BuildAsync(guard.Token);
            var reopened = await recovery.OpenDatabaseAsync("typed", guard.Token);
            reopened.IsOffline.ShouldBeFalse(); reopened.Catalog.PendingMigration.ShouldBeNull();
            reopened.Catalog.SchemaState!.ContentHash.ShouldBe(PauseProbeSchema(false).Hash);
            await using var reader = await reopened.CreateSessionAsync(guard.Token);
            (await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(32768L);
        }
        finally
        {
            _output.WriteLine($"offline probe: injected={injected}, failures={worker.FailureCount}, clock={clock.Elapsed}, offline={database.IsOffline}, outcome={outcome?.GetType().Name}");
            worker.BeforeCheckpoint = null; SqlPlanExecutor.RewriteStage = null; gate.JournalSizeLimit = 0;
        }
    }
}
