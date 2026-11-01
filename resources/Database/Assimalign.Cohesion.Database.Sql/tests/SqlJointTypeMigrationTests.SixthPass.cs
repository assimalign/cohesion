using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Sixth pass: one-byte checkpoint size permits a complete dropped-owner drain")]
    public async Task RunBatch_OneByteCheckpoint_ShouldFinishDroppedTable()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var builder = Builder(new CrashCaptureSqlStorageStrategy());
        builder.Options.CheckpointJournalSize = 1;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("CREATE TABLE retired(payload TEXT)", cancellationToken: guard.Token);
            database.Catalog.TryGetTable("dbo", "retired", out var table).ShouldBeTrue();
            SeedOwner(database, table, [new string('x', 7000)], 96);
            await session.ExecuteAsync("DROP TABLE retired", cancellationToken: guard.Token);
            long reclaimed = database.TableReclamation.ReclaimedPages;
            database.TableReclamation.Request();

            // Act: each cycle finishes a checkpoint, consumes its wake, then polls reclamation.
            for (int cycle = 0; cycle < 8 && database.TableReclamation.HasWork; cycle++)
            {
                database.Coordinator.Checkpoint(guard.Token);
                database.CatalogStorage.Checkpoint();
                engine.CheckpointNeededSignal.Reset();
                database.TableReclamation.RunBatch(guard.Token);
            }

            // Assert
            database.TableReclamation.HasWork.ShouldBeFalse();
            database.TableReclamation.ReclaimedPages.ShouldBe(reclaimed + 96);
            database.DataStorage.HasOwnerPages(table.ObjectId).ShouldBeFalse();
            database.IsOffline.ShouldBeFalse();
        });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sixth pass: due batch commit wakes a refused checkpoint once per cycle")]
    public async Task RunBatch_RefusedCheckpoint_ShouldWakeRetryOncePerCycle()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var builder = Builder(new CrashCaptureSqlStorageStrategy());
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("CREATE TABLE retired(payload TEXT)", cancellationToken: guard.Token);
            database.Catalog.TryGetTable("dbo", "retired", out var table).ShouldBeTrue();
            SeedOwner(database, table, [new string('x', 7000)], 128);
            await session.ExecuteAsync("DROP TABLE retired", cancellationToken: guard.Token);
            database.Coordinator.Checkpoint(guard.Token);
            database.CatalogStorage.Checkpoint();
            database.DataStorage.CheckpointJournalSize = 64 * 1024;
            var worker = engine.Workers.OfType<SqlCheckpointWorker>().Single();
            int refusals = 0, commits = 0;
            database.TableReclamation.BeforeBatchCommit = (_, _) =>
            {
                engine.CheckpointNeededSignal.IsSet.ShouldBeTrue(); // Original crossing wake.
                engine.CheckpointNeededSignal.Reset(); // Worker iteration starts.
                Should.Throw<StorageTransactionException>(() => PollCheckpoint(worker, database, TimeSpan.FromDays(1), guard.Token));
                refusals++;
            };
            database.TableReclamation.AfterBatch = (_, _) => commits++;
            using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
            try
            {
                // Act / Assert: a successful retry is driven by the commit wake, never a timeout.
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    database.TableReclamation.Request();
                    database.TableReclamation.RunBatch(guard.Token);
                    engine.CheckpointNeededSignal.IsSet.ShouldBeTrue();
                    engine.CheckpointNeededSignal.Reset();
                    long length = database.DataStorage.JournalLength;
                    for (int poll = 0; poll < 8; poll++) { database.TableReclamation.RunBatch(guard.Token); }
                    engine.CheckpointNeededSignal.IsSet.ShouldBeFalse();
                    database.DataStorage.JournalLength.ShouldBe(length);
                    commits.ShouldBe(cycle + 1);
                    PollCheckpoint(worker, database, TimeSpan.FromDays(1), guard.Token).ShouldBeTrue();
                    engine.CheckpointNeededSignal.Reset();
                }
                refusals.ShouldBe(2);
                var yields = recorder.Events.Where(e => e.EventId == 27).ToArray();
                yields.Length.ShouldBe(16);
                yields.ShouldAllBe(e => e.EventName == "TableReclamationYielded");
                yields[0].PayloadNames.ShouldBe(["engineName", "database", "objectId"]);
                yields[0].Payload.ShouldBe([engine.Name, "typed", table.ObjectId]);
                recorder.Events.ShouldNotContain(e => e.EventId == 0);
            }
            finally { database.TableReclamation.BeforeBatchCommit = null; database.TableReclamation.AfterBatch = null; }
        });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sixth pass: eager zero-filled BINARY copy admits bracket and publication without pending holes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_EagerBinaryGrowth_ShouldExcludePendingBytes(bool publication)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        builder.Options.BufferPoolCapacity = 4L * 1024 * 1024 * 1024;
        SqlCompiledSchema Declare(bool changed) => Compiled(new CompiledSchemaTable("items", "row",
            [new("Value", changed ? DatabaseType.Int64 : DatabaseType.Int32, false), new("Payload", DatabaseType.Binary, false)], null, [], []));
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            await database.ApplySchemaAsync(Declare(false), guard.Token);
            SeedRaw(database, [1, new byte[3000]], 512);
            database.Coordinator.Checkpoint(guard.Token);
            long copied = 0;
            SqlPlanExecutor.RewriteStage = (stage, count) =>
            {
                if (stage != "Copied") { return; }
                copied = count;
                if (!publication)
                {
                    storage.MigrationAvailableBytes = database.SchemaProvisioner.JointSpaceDiscardBytes +
                        Math.Max(SqlSchemaProvisioner.JointBracketBytes(128, 64), database.SchemaProvisioner.JointSpacePublicationBytes) + 256 * 1024;
                }
            };
            database.SchemaProvisioner.JointStage = stage =>
            {
                if (publication && stage == "TablePrepared")
                { storage.MigrationAvailableBytes = database.SchemaProvisioner.JointSpaceDiscardBytes + database.SchemaProvisioner.JointSpacePublicationBytes; }
            };
            try
            {
                // Act
                await database.ApplySchemaAsync(Declare(true), guard.Token);

                // Assert
                copied.ShouldBe(512);
                database.Catalog.SchemaState!.ContentHash.ShouldBe(Declare(true).Hash);
                storage.CapacityFailures.ShouldBe(0);
                database.IsOffline.ShouldBeFalse();
            }
            finally { SqlPlanExecutor.RewriteStage = null; database.SchemaProvisioner.JointStage = null; storage.MigrationAvailableBytes = long.MaxValue; }
        });
    }

    // UnsafeAccessor keeps these decision-table regressions runnable against the pre-fix assembly.
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "IsSparseGrowth")]
    private static extern bool SparseGrowth(FileSystemSqlStorageStrategy? strategy, bool windows, string path,
        FileAttributes file, FileAttributes directory, DriveType drive);

    [Theory(DisplayName = "Cohesion Test [Sql] - Sixth pass: sparse decision covers every attribute and Windows path class")]
    [InlineData(true, @"C:\db\sql.dat", FileAttributes.Normal, FileAttributes.Directory, DriveType.Fixed, false)]
    [InlineData(false, "/db/sql.dat", FileAttributes.Normal, FileAttributes.Directory, DriveType.Fixed, true)]
    [InlineData(true, @"C:\db\sql.dat", FileAttributes.Compressed, FileAttributes.Directory, DriveType.Fixed, true)]
    [InlineData(true, @"C:\db\sql.dat", FileAttributes.SparseFile, FileAttributes.Directory, DriveType.Fixed, true)]
    [InlineData(true, @"C:\db\sql.dat", FileAttributes.IntegrityStream, FileAttributes.Directory, DriveType.Fixed, true)]
    [InlineData(true, @"C:\db\sql.dat", FileAttributes.Normal, FileAttributes.Compressed, DriveType.Fixed, true)]
    [InlineData(true, @"C:\db\sql.dat", FileAttributes.Normal, FileAttributes.SparseFile, DriveType.Fixed, true)]
    [InlineData(true, @"C:\db\sql.dat", FileAttributes.Normal, FileAttributes.IntegrityStream, DriveType.Fixed, true)]
    [InlineData(true, @"\\server\share\db\sql.dat", FileAttributes.Normal, FileAttributes.Directory, DriveType.Fixed, true)]
    [InlineData(true, @"\\wsl$\Ubuntu\db\sql.dat", FileAttributes.Normal, FileAttributes.Directory, DriveType.Fixed, true)]
    [InlineData(true, @"\\?\UNC\server\share\sql.dat", FileAttributes.Normal, FileAttributes.Directory, DriveType.Fixed, true)]
    [InlineData(true, @"\\?\C:\db\sql.dat", FileAttributes.Normal, FileAttributes.Directory, DriveType.Fixed, false)]
    [InlineData(true, @"Z:\db\sql.dat", FileAttributes.Normal, FileAttributes.Directory, DriveType.Network, true)]
    [InlineData(true, @"C:\mount\db\sql.dat", FileAttributes.Normal, FileAttributes.Directory, DriveType.Unknown, true)]
    public void SparseDataGrowth_FileAndVolumeClasses_ShouldChargeLazyGrowth(bool windows, string path, FileAttributes file,
        FileAttributes directory, DriveType drive, bool expected)
    {
        // Arrange / Act / Assert
        SparseGrowth(null, windows, path, file, directory, drive).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sixth pass: a reclamation commit between copy samples preserves exact allocation pairing")]
    public async Task ApplyAsync_IntraBracketReclamation_ShouldPairAllocationSamples()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var builder = Builder(new CrashCaptureSqlStorageStrategy());
        builder.Options.CheckpointJournalSize = 0;
        builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using var engine = await builder.BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
        {
            await database.ApplySchemaAsync(NarrowDeclaration(false, false), guard.Token);
            SeedRaw(database, [1, 1], 64);
            await using var session = await database.CreateSessionAsync(guard.Token);
            await session.ExecuteAsync("CREATE TABLE scratch(payload TEXT)", cancellationToken: guard.Token);
            database.Catalog.TryGetTable("dbo", "scratch", out var scratch).ShouldBeTrue();
            SeedOwner(database, scratch, [new string('x', 7000)], 64);
            long epoch = database.TableReclamation.EnterStatement();
            await session.ExecuteAsync("DROP TABLE scratch", cancellationToken: guard.Token);
            database.TableReclamation.RunBatch(guard.Token); // Fence precedes the copy's reader epoch.
            database.TableReclamation.ExitStatement(epoch);
            database.Coordinator.Checkpoint(guard.Token);
            long reclaimed = database.TableReclamation.ReclaimedPages;
            int rendezvous = 0;
            long copiedAllocations = -1;
            SqlPlanExecutor.RewriteStage = (stage, count) =>
            {
                if (stage == "CopyAllocationsCaptured")
                {
                    rendezvous++;
                    Task.Run(() => database.TableReclamation.RunBatch(guard.Token), guard.Token).Wait(guard.Token);
                    database.TableReclamation.ReclaimedPages.ShouldBe(reclaimed + 32);
                    database.TableReclamation.CaptureAllocationCount().ShouldBe(count);
                }
                if (stage == "CopyAllocationsAdded") { copiedAllocations = count; }
            };
            try
            {
                // Act
                await database.ApplySchemaAsync(NarrowDeclaration(true, false), guard.Token);

                // Assert
                rendezvous.ShouldBe(1);
                copiedAllocations.ShouldBe(1);
                database.TableReclamation.ReclaimedPages.ShouldBe(reclaimed + 32);
                database.Catalog.SchemaState!.ContentHash.ShouldBe(NarrowDeclaration(true, false).Hash);
                database.IsOffline.ShouldBeFalse();
            }
            finally { SqlPlanExecutor.RewriteStage = null; }
        });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sixth pass: residual risk names the drain, margin, truncation window and undetected allocation modes")]
    public void Design_ResidualRisk_ShouldDescribeTheWholeExposure()
    {
        // Arrange
        static string DesignPath([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../docs/DESIGN.md"));
        string design = File.ReadAllText(DesignPath());

        // Act / Assert
        design.ShouldContain("during the whole reclamation drain after a stop or publication", Case.Sensitive);
        design.ShouldContain("max(1 MiB,ceil(E0/4))", Case.Sensitive);
        design.ShouldContain("from C until truncation", Case.Sensitive);
        design.ShouldContain("possibly stalled", Case.Sensitive);
        design.ShouldContain("undetected copy-on-write, deduplication, thin provisioning", Case.Sensitive);
    }
}
