using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Copy checkpoint fix: sparse engine-flushed pages publish at exact admission (#1778)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyAsync_CheckpointFixSparseExactAdmission_ShouldPublish(bool keptOwner)
    {
        // Arrange: reuse one durable source image for unlimited and exactly admitted runs.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        const long pool = 64L * 1024 * 1024;
        long budget = keptOwner ? 32L * 1024 * 1024 : 1024 * 1024;
        var source = keptOwner
            ? SqlSchema.Create("typed", schema => schema.Table<OtherRow>("items", table => table.Key(row => row.Id))).Compile()
            : WideCheckpointDeclaration(false);
        var target = keptOwner ? ChangeItems(source, indexes: [new("ix", ["Id"], false)]) : WideCheckpointDeclaration(true);
        var image = new CrashCaptureSqlStorageStrategy();
        var seedBuilder = Builder(image);
        seedBuilder.Options.BufferPoolCapacity = pool;
        seedBuilder.Options.CheckpointJournalSize = budget;
        seedBuilder.Options.CheckpointInterval = TimeSpan.FromDays(1);
        await using (var seed = await seedBuilder.BuildAsync(guard.Token))
        {
            var database = await seed.CreateDatabaseAsync("typed", guard.Token);
            await database.ApplySchemaAsync(source, guard.Token);
            if (keptOwner) { await SeedShuffledIndexRowsAsync(database, 393216, guard.Token); }
            else { SeedRaw(database, [1, new string('x', 4000)], 16384); }
            database.Coordinator.Checkpoint(guard.Token);
            image = image.CaptureDurableImages();
        }
        long need = 0;
        for (int run = 0; run < 2; run++)
        {
            var storage = image.CaptureDurableImages();
            storage.UnixSparseGrowth = true;
            var builder = Builder(storage);
            builder.Options.BufferPoolCapacity = pool;
            builder.Options.CheckpointJournalSize = budget;
            builder.Options.CheckpointInterval = TimeSpan.FromDays(1);
            builder.Options.PageWriteBackInterval = TimeSpan.FromDays(1);
            await using var engine = await builder.BuildAsync(guard.Token);
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            await WithPausedMaintenanceAsync(engine, guard.Token, async () =>
            {
                database.Coordinator.Checkpoint(guard.Token);
                ulong owner = Table(database).ObjectId;
                var checkpointSource = EventSource.GetSources().Single(value => value.Name == "Assimalign.Cohesion.Database.Storage");
                using var events = new EventSourceRecorder(checkpointSource, EventLevel.Informational);
                int forced = 0, observed = 0;
                long allocationStart = database.TableReclamation.CaptureAllocationCount();
                long copied = 0, peak = 0;
                database.SchemaProvisioner.JointStage = stage =>
                {
                    if (stage == "OwnersDurable" && run == 1) { storage.VolumeCapacityBytes = storage.AllocatedBytes + need; }
                };
                storage.DurableStorageChanged = catalog =>
                { if (!catalog) { peak = Math.Max(peak, database.DataStorage.JournalLength); } };
                SqlPlanExecutor.RewriteStage = (stage, value) =>
                {
                    if (stage == "CopyCheckpointed") { forced++; }
                    if (stage == "CopyCheckpointObserved") { observed++; }
                    if (stage == "Copied") { copied = value; }
                };
                try
                {
                    // Act
                    var failure = await Record.ExceptionAsync(async () =>
                    {
                        if (keptOwner)
                        {
                            await database.SchemaProvisioner.ApplyAsync(target, guard.Token, deferDataScalingSteps: true);
                            await WaitDoneAsync(database, guard.Token);
                        }
                        else { await database.ApplySchemaAsync(target, guard.Token); }
                    });
                    long allocations = database.TableReclamation.CaptureAllocationCount() - allocationStart;
                    if (run == 0) { need = database.SchemaProvisioner.JointSpaceNeed; }
                    int completed = events.Events.Count(entry => entry.EventId == 6 &&
                        Equals(entry.Payload![0], database.DataStorage.Name.ToString()) && Equals(entry.Payload[1], "Success"));
                    _output.WriteLine($"Sparse exact: kept={keptOwner}; exact={run == 1}; N={need}; T={database.SchemaProvisioner.JointCopyCheckpointThreshold}; allocated pages={allocations}; copied={copied}; peak={peak}; completed={completed}; forced={forced}; observed={observed}; failure={failure?.Message ?? database.Provisioning?.FailureCode ?? "none"}");

                    // Assert: an engine checkpoint really pre-empted a copy flush.
                    failure.ShouldBeNull();
                    database.Provisioning.ShouldBeNull();
                    completed.ShouldBeGreaterThan(0);
                    allocations.ShouldBeGreaterThan(0);
                    forced.ShouldBe(0);
                    if (keptOwner)
                    {
                        observed.ShouldBeGreaterThan(0);
                        database.SchemaProvisioner.JointCopyCheckpointThreshold.ShouldBe(budget);
                        (allocations * Page.Size).ShouldBeLessThan(pool);
                        Table(database).ObjectId.ShouldBe(owner);
                    }
                    database.SchemaProvisioner.JointSpaceNeed.ShouldBe(need);
                    database.Catalog.SchemaState!.ContentHash.ShouldBe(target.Hash);
                    database.IsOffline.ShouldBeFalse();
                    storage.CapacityFailures.ShouldBe(0);
                }
                finally
                {
                    SqlPlanExecutor.RewriteStage = null;
                    storage.DurableStorageChanged = null;
                    database.SchemaProvisioner.JointStage = null;
                    storage.VolumeCapacityBytes = long.MaxValue;
                }
            });
        }
    }
}
