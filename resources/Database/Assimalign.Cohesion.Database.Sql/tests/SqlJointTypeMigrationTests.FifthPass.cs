using System;
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
            await Task.WhenAll(purgeEntered.Task, checkpointEntered.Task).WaitAsync(token);
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
            long peak = 0, peakCycle = 0;
            int refused = 0, refusedCycle = 0, checkpoints = 0, batches = 0;
            var worker = engine.Workers.OfType<SqlCheckpointWorker>().Single();
            database.TableReclamation.BeforeBatchCommit = (_, _) =>
            {
                peak = Math.Max(peak, database.DataStorage.JournalLength);
                if (database.DataStorage.JournalLength >= threshold)
                {
                    Should.Throw<StorageTransactionException>(() => PollCheckpoint(worker, database, TimeSpan.FromDays(1), guard.Token));
                    refused++;
                    refusedCycle++;
                }
            };
            database.TableReclamation.AfterBatch = (_, _) =>
            { batches++; peak = Math.Max(peak, database.DataStorage.JournalLength); };
            try
            {
                // Act: a pending attempt below the size threshold must also get an idle poll.
                if (pendingOnly)
                {
                    engine.CheckpointNeededSignal.Set();
                    database.TableReclamation.Request();
                    database.TableReclamation.RunBatch(guard.Token);
                    batches.ShouldBe(0);
                    engine.CheckpointNeededSignal.Reset();
                }
                database.TableReclamation.Request();
                while (database.TableReclamation.HasWork)
                {
                    guard.Token.ThrowIfCancellationRequested();
                    database.TableReclamation.RunBatch(guard.Token);
                    if (database.DataStorage.JournalLength < threshold) { continue; }
                    long before = database.DataStorage.JournalLength;
                    int beforeBatches = batches;
                    // Repeated requests/polls cannot add even another owner's bracket.
                    for (int poll = 0; poll < 8; poll++)
                    {
                        database.TableReclamation.Request();
                        database.TableReclamation.RunBatch(guard.Token);
                    }
                    database.DataStorage.JournalLength.ShouldBe(before);
                    batches.ShouldBe(beforeBatches);
                    refusedCycle.ShouldBeLessThanOrEqualTo(1);
                    PollCheckpoint(worker, database, TimeSpan.FromDays(1), guard.Token).ShouldBeTrue();
                    engine.CheckpointNeededSignal.Reset();
                    checkpoints++;
                    peakCycle = Math.Max(peakCycle, refusedCycle);
                    refusedCycle = 0;
                }

                // Assert: one in-flight bracket, independent of owners or requests.
                long overshoot = 32L * 2 * (Page.Size + 46) + 152;
                peak.ShouldBeLessThanOrEqualTo(threshold + overshoot);
                peakCycle.ShouldBeLessThanOrEqualTo(1);
                refused.ShouldBeGreaterThan(0);
                checkpoints.ShouldBeGreaterThan(0);
                database.TableReclamation.ReclaimedPages.ShouldBe(reclaimed + pages);
                database.IsOffline.ShouldBeFalse();
                _output.WriteLine($"reclaim pages={pages}; peak={peak}; bound={threshold + overshoot}; refused={refused}; cycles={checkpoints}; max refused/cycle={peakCycle}");
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
        var storage = new CrashCaptureSqlStorageStrategy();
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
                // allocates none. Follow the copy's recorded flushes exactly.
                if (stage == "CopyCommitted" && treesDurable) { pendingPages += 64; }
                if (stage == "CopyCheckpointed") { pendingPages = 0; }
                if (stage == "Copied" && prepared == 7) { copiedLast = count; }
                if (stage == "Copied" && prepared == 7 && count == 64)
                {
                    // Exactly D + pending + E remains. Admit the next bracket
                    // at equality, then stop before the following one can write.
                    long pending = Math.Min(pendingPages * Page.Size, builder.Options.BufferPoolCapacity);
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
                copiedLast.ShouldBe(128);
                remaining.ShouldBeGreaterThan(0);
                SqlPlanExecutor.RewriteStage = null;
                database.CatalogStorage.Checkpoint();
                var worker = engine.Workers.OfType<SqlCheckpointWorker>().Single();
                PollCheckpoint(worker, database, TimeSpan.FromDays(1), guard.Token).ShouldBeTrue();
                engine.CheckpointNeededSignal.Reset();
                long reclaimed = database.TableReclamation.ReclaimedPages;
                long peak = 0;
                int checkpoints = 0;
                database.TableReclamation.Request();
                while (database.TableReclamation.HasWork)
                {
                    guard.Token.ThrowIfCancellationRequested();
                    database.TableReclamation.RunBatch(guard.Token);
                    peak = Math.Max(peak, database.DataStorage.JournalLength);
                    if (database.DataStorage.JournalLength < 1024 * 1024) { continue; }
                    // Let several reclamation polls run before the next checkpoint.
                    for (int poll = 0; poll < 8; poll++) { database.TableReclamation.RunBatch(guard.Token); }
                    peak = Math.Max(peak, database.DataStorage.JournalLength);
                    PollCheckpoint(worker, database, TimeSpan.FromDays(1), guard.Token).ShouldBeTrue();
                    engine.CheckpointNeededSignal.Reset();
                    checkpoints++;
                }
                database.Coordinator.Checkpoint(guard.Token);

                // Assert
                peak.ShouldBeLessThanOrEqualTo(1024 * 1024 + 32L * 2 * (Page.Size + 46) + 152);
                checkpoints.ShouldBeGreaterThan(1);
                (database.TableReclamation.ReclaimedPages - reclaimed).ShouldBeGreaterThanOrEqualTo(7 * 1024 + 128);
                storage.CapacityFailures.ShouldBe(0);
                database.IsOffline.ShouldBeFalse();
                database.Catalog.SchemaState!.ContentHash.ShouldBe(Declare(false).Hash);
                _output.WriteLine($"eight-table stop: exact watchdog need={remaining}; copied in last table={copiedLast}; peak WAL={peak}; checkpoints={checkpoints}; reclaimed={database.TableReclamation.ReclaimedPages - reclaimed}; capacity failures={storage.CapacityFailures}");
            }
            finally { SqlPlanExecutor.RewriteStage = null; database.SchemaProvisioner.JointStage = null; storage.VolumeCapacityBytes = long.MaxValue; }
        });
    }
}
