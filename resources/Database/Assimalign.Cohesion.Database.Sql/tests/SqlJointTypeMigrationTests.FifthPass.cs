using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Tests;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    // Exercise the existing checkpointer's due/checkpoint path without its clock.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Checkpoint")]
    private static extern bool PollCheckpoint(SqlCheckpointWorker worker, SqlDatabase database, TimeSpan interval, CancellationToken token);

    private static void SeedOwner(SqlDatabase database, SqlCatalogTable table, object?[] values, int count)
    {
        byte[] record = SqlRowCodec.Encode(table, values, TransactionSequence.None);
        for (int first = 0; first < count; first += 64)
        {
            using var bracket = database.DataStorage.BeginTransaction();
            for (int row = first; row < Math.Min(first + 64, count); row++)
            { database.DataStorage.InsertRow(bracket, table.ObjectId, record); }
            bracket.Commit();
        }
    }

    private static async Task WithPausedMaintenanceAsync(SqlDatabaseEngine engine, CancellationToken token, Func<Task> action)
    {
        using var release = new ManualResetEventSlim();
        var purgeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkpointEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var purge = WorkerPumpObservation.Create(engine.Workers.OfType<SqlVersionPurgeWorker>().Single(), _ => { },
            _ => { purgeEntered.TrySetResult(); release.Wait(token); });
        using var checkpoint = WorkerPumpObservation.Create(engine.Workers.OfType<SqlCheckpointWorker>().Single(), _ => { },
            _ => { checkpointEntered.TrySetResult(); release.Wait(token); });
        try
        {
            engine.UndoDeferredSignal.Set();
            engine.CheckpointNeededSignal.Set();
            var entered = Task.WhenAll(purgeEntered.Task, checkpointEntered.Task);
            while (!entered.IsCompleted)
            {
                // A worker may still be completing a pass that consumes the first wake.
                engine.UndoDeferredSignal.Set();
                engine.CheckpointNeededSignal.Set();
                await Task.WhenAny(entered, Task.Delay(1, token));
                token.ThrowIfCancellationRequested();
            }
            await entered;
            engine.CheckpointNeededSignal.Reset();
            await action();
        }
        finally { release.Set(); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Reclamation yields every owner bracket to a due or pending checkpoint")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunBatch_DueCheckpoint_ShouldBoundJournalAndFinish(bool pendingOnly)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            await using var session = await database.CreateSessionAsync(guard.Token);
            long pages = 0;
            for (int owner = 0; owner < 8; owner++)
            {
                await session.ExecuteAsync($"CREATE TABLE retired{owner}(payload TEXT)", cancellationToken: guard.Token);
                database.Catalog.TryGetTable("dbo", $"retired{owner}", out var table).ShouldBeTrue();
                SeedOwner(database, table, [new string('x', 7000)], 128);
                pages += database.DataStorage.GetOwnerPages(table.ObjectId).Count;
                await session.ExecuteAsync($"DROP TABLE retired{owner}", cancellationToken: guard.Token);
            }
            database.Coordinator.Checkpoint(guard.Token);
            database.CatalogStorage.Checkpoint();
            const long threshold = 1024 * 1024;
            database.DataStorage.CheckpointJournalSize = threshold;
            database.CatalogStorage.CheckpointJournalSize = 0;
            long reclaimed = database.TableReclamation.ReclaimedPages;
            long peak = 0;
            int batches = 0;
            var source = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Storage");
            using var checkpoints = new Assimalign.Cohesion.Testing.EventSourceRecorder(source, EventLevel.Verbose);
            database.TableReclamation.BeforeBatchCommit = (_, _) =>
            { peak = Math.Max(peak, database.DataStorage.JournalLength); };
            database.TableReclamation.AfterBatch = (_, _) =>
            { batches++; peak = Math.Max(peak, database.DataStorage.JournalLength); };
            try
            {
                // Act: a pending wake below the size threshold still yields before admission.
                if (pendingOnly)
                {
                    engine.CheckpointNeededSignal.Set();
                    database.TableReclamation.Request();
                    database.TableReclamation.RunBatch(guard.Token);
                    batches.ShouldBe(0);
                }
                database.TableReclamation.Request();
                while (database.TableReclamation.HasWork)
                {
                    guard.Token.ThrowIfCancellationRequested();
                    // The worker is deliberately paused. Acknowledge each crossing wake;
                    // release itself must have checkpointed before another bracket can run.
                    engine.CheckpointNeededSignal.Reset();
                    database.TableReclamation.RunBatch(guard.Token);
                    database.DataStorage.IsCheckpointDue(TimeSpan.MaxValue).ShouldBeFalse();
                }

                // Assert: sample each actual checkpoint BEFORE truncation, including commit WAL.
                var completed = checkpoints.Events.Where(e => e.EventId == 6 && Equals(e.Payload![0], (string)database.DataStorage.Name)
                    && Equals(e.Payload![1], "Success")).ToArray();
                foreach (var checkpoint in checkpoints.Events.Where(e => e.EventId == 5 && Equals(e.Payload![0], (string)database.DataStorage.Name)))
                { peak = Math.Max(peak, (long)checkpoint.Payload![2]!); }
                long overshoot = 32L * 2 * (Page.Size + 46) + 152;
                peak.ShouldBeLessThanOrEqualTo(threshold + overshoot);
                completed.Length.ShouldBeGreaterThan(0);
                checkpoints.Events.ShouldNotContain(e => e.EventId == 0);
                batches.ShouldBeGreaterThan(0);
                database.TableReclamation.ReclaimedPages.ShouldBe(reclaimed + pages);
                database.IsOffline.ShouldBeFalse();
                _output.WriteLine($"reclaim pages={pages}; peak={peak}; bound={threshold + overshoot}; release checkpoints={completed.Length}; batches={batches}");
            }
            finally { database.TableReclamation.BeforeBatchCommit = null; database.TableReclamation.AfterBatch = null; }
        });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joint reserve charges a large dirty pool only for sparse growth")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_FourGiBPool_ShouldFollowStorageGrowthMode(bool unix)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy { UnixSparseGrowth = unix };
        var builder = Builder(storage);
        builder.Options.BufferPoolCapacity = 4L * 1024 * 1024 * 1024;
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await database.ApplySchemaAsync(NarrowDeclaration(false, false), guard.Token);
        SeedRaw(database, [1, 1], 1000);
        database.Coordinator.Checkpoint(guard.Token);
        storage.VolumeCapacityBytes = storage.AllocatedBytes + 4L * 1024 * 1024 * 1024;
        try
        {
            // Act / Assert
            if (unix)
            {
                var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
                    await database.ApplySchemaAsync(NarrowDeclaration(true, false), guard.Token));
                failure.Diagnostics.ShouldContain(d => d.Code == "CS03402");
                database.SchemaProvisioner.JointSpaceDiscardBytes.ShouldBeGreaterThan(4L * 1024 * 1024 * 1024);
            }
            else
            {
                await database.ApplySchemaAsync(NarrowDeclaration(true, false), guard.Token);
                database.Catalog.SchemaState!.ContentHash.ShouldBe(NarrowDeclaration(true, false).Hash);
                database.SchemaProvisioner.JointSpaceDiscardBytes.ShouldBeLessThan(64L * 1024 * 1024);
            }
            storage.CapacityFailures.ShouldBe(0);
            database.IsOffline.ShouldBeFalse();
        }
        finally { storage.VolumeCapacityBytes = long.MaxValue; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint heap watchdog charges a different existing page per small row")]
    public async Task ApplyAsync_SmallRowsFillOldGaps_ShouldChargeEveryHeapTouch()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy { UnixSparseGrowth = true };
        var builder = Builder(storage);
        builder.Options.CheckpointJournalSize = 0;
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        SqlCompiledSchema Declare(bool changed) => Compiled(new CompiledSchemaTable("items", "row",
            [new("Value", changed ? DatabaseType.Int32 : DatabaseType.Int64, false), new("Payload", DatabaseType.String, false)], null, [], []));
        await database.ApplySchemaAsync(Declare(false), guard.Token);
        var source = Table(database);
        int largeBytes = SqlRowCodec.Encode(source, [1L, new string('x', 2600)], TransactionSequence.None).Length + 4;
        int emptyBytes = SqlRowCodec.Encode(source, [1L, ""], TransactionSequence.None).Length + 4;
        int gap = SlottedPage.BodySize - 3 * largeBytes;
        // Small source rows miss the old gaps by one byte, so the scan visits
        // them last. Narrowing BIGINT creates twelve more bytes per target gap.
        SeedRaw(database, [1L, new string('x', 2600)], 192);
        SeedRaw(database, [1L, new string('y', gap + 1 - emptyBytes)], 64);
        database.Coordinator.Checkpoint(guard.Token);
        long copied = 0;
        long heapPages = 0;
        ulong preparedOwner = 0;
        database.SchemaProvisioner.JointStage = stage =>
        {
            if (stage == "OwnersDurable")
            { preparedOwner = database.Catalog.CaptureTableReclamations().Single().ObjectId; }
        };
        SqlPlanExecutor.RewriteStage = (stage, count) =>
        {
            if (stage != "Copied") { return; }
            copied = count;
            if (count == 192)
            {
                heapPages = database.DataStorage.GetOwnerPages(preparedOwner).Count;
                database.Coordinator.Checkpoint(guard.Token);
                storage.MigrationAvailableBytes = database.SchemaProvisioner.JointSpaceDiscardBytes + heapPages * Page.Size + 512 * 1024;
            }
        };
        try
        {
            // Act
            var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
                await database.ApplySchemaAsync(Declare(true), guard.Token));

            // Assert
            copied.ShouldBe(192);
            heapPages.ShouldBe(64);
            failure.Diagnostics.ShouldContain(d => d.Code == "CS03402");
            failure.Message.ShouldContain("bytes needed");
            database.Catalog.SchemaState!.ContentHash.ShouldBe(Declare(false).Hash);
            database.IsOffline.ShouldBeFalse();
            _output.WriteLine($"heap copied={copied}; pages={heapPages}; {failure.Message}");
        }
        finally { SqlPlanExecutor.RewriteStage = null; database.SchemaProvisioner.JointStage = null; storage.MigrationAvailableBytes = long.MaxValue; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint copy never owns pages freed between its brackets")]
    public async Task ApplyAsync_InterBracketReclamation_ShouldCountOnlyCopyAllocations()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            await database.ApplySchemaAsync(NarrowDeclaration(false, false), guard.Token);
            SeedRaw(database, [1, 1], 4096);
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("CREATE TABLE scratch(payload TEXT)", cancellationToken: guard.Token);
            database.Catalog.TryGetTable("dbo", "scratch", out var scratch).ShouldBeTrue();
            SeedOwner(database, scratch, [new string('x', 7000)], 4096);
            long epoch = database.TableReclamation.EnterStatement();
            await session.ExecuteAsync("DROP TABLE scratch", cancellationToken: guard.Token);
            database.TableReclamation.RunBatch(guard.Token); // Discover before the copy's epoch.
            database.TableReclamation.ExitStatement(epoch);
            database.Coordinator.Checkpoint(guard.Token);
            long reclaimed = database.TableReclamation.ReclaimedPages;
            long firstReserve = 0, peakReserve = 0;
            SqlPlanExecutor.RewriteStage = (stage, _) =>
            {
                if (stage != "Copied") { return; }
                if (firstReserve == 0) { firstReserve = database.SchemaProvisioner.JointSpaceDiscardBytes; }
                peakReserve = Math.Max(peakReserve, database.SchemaProvisioner.JointSpaceDiscardBytes);
                database.TableReclamation.RunBatch(guard.Token);
                database.TableReclamation.RunBatch(guard.Token);
            };
            try
            {
                // Act
                await database.ApplySchemaAsync(NarrowDeclaration(true, false), guard.Token);

                // Assert
                (database.TableReclamation.ReclaimedPages - reclaimed).ShouldBe(4096);
                peakReserve.ShouldBeLessThanOrEqualTo(firstReserve + 1024 * 1024);
                database.Catalog.SchemaState!.ContentHash.ShouldBe(NarrowDeclaration(true, false).Hash);
                database.IsOffline.ShouldBeFalse();
                _output.WriteLine($"inter-bracket frees=4096; first E={firstReserve}; peak E={peakReserve}");
            }
            finally { SqlPlanExecutor.RewriteStage = null; }
        });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint watchdog retains physical headroom while eight prepared tables are reclaimed")]
    public async Task ApplyAsync_EightTableStopAtReserve_ShouldReclaimWithoutCapacityFailure()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.BufferPoolCapacity = 1024 * 1024;
        builder.Options.CheckpointJournalSize = 1024 * 1024;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        var domain = SqlType.CreateDomain("expanded", SqlType.Text);
        builder.Types.Add(domain);
        builder.Types.AddCast(SqlCast.Create<int, string>(SqlType.Integer, domain, _ => new string('x', 7000), SqlCastContext.Assignment));
        SqlCompiledSchema Declare(bool changed) => Compiled(Enumerable.Range(0, 8).Select(table =>
            new CompiledSchemaTable($"items{table}", $"row{table}", [new("Value", changed ? DatabaseType.String : DatabaseType.Int32,
                false, CustomType: changed ? "expanded" : null)], null, [], [])).ToArray());
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            await database.ApplySchemaAsync(Declare(false), guard.Token);
            foreach (var table in database.Catalog.Tables) { SeedOwner(database, table, [1], 1024); }
            database.Coordinator.Checkpoint(guard.Token);
            int prepared = 0;
            long remaining = 0, copiedLast = 0, pendingPages = 0;
            bool treesDurable = false;
            database.SchemaProvisioner.JointStage = stage =>
            {
                if (stage == "TreesDurable") { treesDurable = true; }
                if (stage == "TablePrepared") { prepared++; }
            };
            SqlPlanExecutor.RewriteStage = (stage, count) =>
            {
                // Every expanded row allocates one heap page; the root bracket
                // allocates none. Follow forced and observed engine checkpoint resets exactly.
                if (stage == "CopyCommitted" && treesDurable) { pendingPages += 64; }
                if (stage is "CopyCheckpointed" or "CopyCheckpointObserved") { pendingPages = 0; }
                if (stage == "Copied" && prepared == 7) { copiedLast = count; }
                if (stage == "Copied" && prepared == 7 && count == 64)
                {
                    // Exactly D + pending + E remains. The engine's checkpoint
                    // recovers room for a further bracket without a duplicate forced
                    // flush; the next admission still refuses before physical ENOSPC.
                    long pending = storage.UnixSparseGrowth ? Math.Min(pendingPages * Page.Size, builder.Options.BufferPoolCapacity) : 0;
                    remaining = SqlSchemaProvisioner.JointBracketBytes(128, 64) + pending + database.SchemaProvisioner.JointSpaceDiscardBytes;
                    storage.VolumeCapacityBytes = storage.AllocatedBytes + remaining;
                }
            };
            try
            {
                // Act
                var failure = await Should.ThrowAsync<SqlSchemaMigrationException>(async () =>
                    await database.ApplySchemaAsync(Declare(true), guard.Token));
                failure.Diagnostics.ShouldContain(d => d.Code == "CS03402");
                prepared.ShouldBe(7);
                // Nit 9 avoids a redundant checkpoint after the engine truncated WAL.
                // That saved WAL admits one more 64-row bracket at the same volume cap.
                copiedLast.ShouldBe(192);
                remaining.ShouldBeGreaterThan(0);
                SqlPlanExecutor.RewriteStage = null;
                database.CatalogStorage.Checkpoint();
                var worker = engine.Workers.OfType<SqlCheckpointWorker>().Single();
                PollCheckpoint(worker, database, TimeSpan.FromDays(1), guard.Token).ShouldBeTrue();
                engine.CheckpointNeededSignal.Reset();
                long reclaimed = database.TableReclamation.ReclaimedPages;
                long peak = 0;
                var checkpointSource = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Storage");
                using var drainCheckpoints = new Assimalign.Cohesion.Testing.EventSourceRecorder(checkpointSource, EventLevel.Verbose);
                database.TableReclamation.Request();
                while (database.TableReclamation.HasWork)
                {
                    guard.Token.ThrowIfCancellationRequested();
                    engine.CheckpointNeededSignal.Reset();
                    database.TableReclamation.RunBatch(guard.Token);
                    peak = Math.Max(peak, database.DataStorage.JournalLength);
                    database.DataStorage.IsCheckpointDue(TimeSpan.MaxValue).ShouldBeFalse();
                }
                var completed = drainCheckpoints.Events.Where(e => e.EventId == 6 && Equals(e.Payload![0], (string)database.DataStorage.Name)
                    && Equals(e.Payload![1], "Success")).ToArray();
                int checkpoints = completed.Length;
                foreach (var checkpoint in drainCheckpoints.Events.Where(e => e.EventId == 5 && Equals(e.Payload![0], (string)database.DataStorage.Name)))
                { peak = Math.Max(peak, (long)checkpoint.Payload![2]!); }
                database.Coordinator.Checkpoint(guard.Token);

                // Assert
                peak.ShouldBeLessThanOrEqualTo(1024 * 1024 + 32L * 2 * (Page.Size + 46) + 152);
                checkpoints.ShouldBeGreaterThan(1);
                (database.TableReclamation.ReclaimedPages - reclaimed).ShouldBeGreaterThanOrEqualTo(7 * 1024 + copiedLast);
                storage.CapacityFailures.ShouldBe(0);
                database.IsOffline.ShouldBeFalse();
                database.Catalog.SchemaState!.ContentHash.ShouldBe(Declare(false).Hash);
                _output.WriteLine($"eight-table stop: exact watchdog need={remaining}; copied in last table={copiedLast}; peak WAL={peak}; checkpoints={checkpoints}; reclaimed={database.TableReclamation.ReclaimedPages - reclaimed}; capacity failures={storage.CapacityFailures}");
            }
            finally { SqlPlanExecutor.RewriteStage = null; database.SchemaProvisioner.JointStage = null; storage.VolumeCapacityBytes = long.MaxValue; }
        });
    }
}
