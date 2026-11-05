using System;
using System.Collections.Generic;
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
    [Theory(DisplayName = "Cohesion Test [Sql] - Joint deferred fix: failed prepared owners and trees converge through every discard and drain image in four reopen modes")]
    [InlineData(StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.Grouped)]
    [InlineData(StorageCommitDurability.None)]
    public async Task OpenAsync_DeferredFixFailedPreparationCorpus_ShouldConvergeInFourModes(StorageCommitDurability durability)
    {
        // Arrange: record once per durability contract, then reuse each image for all declarations.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var storage = new CrashCaptureSqlStorageStrategy(durability);
        CrashCaptureSqlStorageStrategy.CrashPointRecorder? recorder = null;
        int prepared = 0;
        ulong heap;
        await using (var engine = await Builder(storage).AddDatabase(DeferredDeclaration(false)).BuildAsync(guard.Token))
        {
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            heap = Table(database).ObjectId;
            await writer.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
            await writer.ExecuteAsync("INSERT INTO filled VALUES(1)", cancellationToken: guard.Token);
            database.SchemaProvisioner.JointStage = phase =>
            {
                if (phase != "TablePrepared") { return; }
                prepared++;
                database.Catalog.PendingMigration.ShouldNotBeNull();
                var registrations = database.IndexManager.ExportRegistrations(includePrepared: true);
                registrations.ShouldContain(value => !database.Catalog.Tables.Any(table => table.ObjectId == value.ObjectId));
                registrations.ShouldContain(value => value.ObjectId == Table(database).ObjectId && database.IndexManager.PageOwner(value) != value.ObjectId);
                // The completed copy and alternate tree have durable owners before
                // the injected failure enters discard. No intent-only forgery.
                recorder = storage.RecordCrashPoints();
                throw new InvalidOperationException("injected prepared-table failure");
            };

            // Act
            await database.SchemaProvisioner.ApplyAsync(DeferredDeclaration(true, true).Compile(), guard.Token, deferDataScalingSteps: true);
            await WaitDoneAsync(database, guard.Token);
            database.Provisioning!.FailureCode.ShouldBe("CS03402");
            await DrainReclamationAsync(database, guard.Token);
            recorder!.Dispose();
        }
        // Assert
        prepared.ShouldBe(1);
        recorder.ShouldNotBeNull().Count.ShouldBeGreaterThan(1);
        for (int point = 0; point < recorder.Count; point++)
        {
            for (int mode = 0; mode < 4; mode++)
            { await CheckDeferredCrashImageAsync(recorder.Open(point), true, mode, heap, guard.Token, 1); }
        }
        _output.WriteLine($"Failed preparation {durability}: images={recorder.Count}; reopens={recorder.Count * 4}");
    }

    private sealed record DeferredCheckpointRow(int Value, string Payload, int Added);
    private static SqlSchema DeferredCheckpointDeclaration(bool target, bool third = false) => SqlSchema.Create("typed", schema =>
    {
        schema.AllowDestructiveChanges();
        schema.Table<DeferredCheckpointRow>("items", table =>
        {
            table.Key(row => row.Value);
            if (third) { table.Index(row => row.Value); }
            table.Column(row => row.Payload, "VARCHAR(4000)");
            if (target) { table.Column(row => row.Added); table.Default(row => row.Added, 7); }
        });
        if (third) { schema.Table<DeferredThird>("third", table => table.Key(row => row.Id)); }
    });

    [Fact(DisplayName = "Cohesion Test [Sql] - Joint deferred fix: a multi-bracket copy crossing a checkpoint reopens with no, matching, third and rollback declarations")]
    public async Task OpenAsync_DeferredFixCheckpointCorpus_ShouldConvergeInFourModes()
    {
        // Arrange: retain named checkpoint/table images instead of recording every
        // wide-page write, which would duplicate hundreds of MiB per boundary.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var storage = new CrashCaptureSqlStorageStrategy();
        var images = new List<CrashCaptureSqlStorageStrategy>();
        int brackets = 0, checkpoints = 0;
        await using (var engine = await Builder(storage).AddDatabase(DeferredCheckpointDeclaration(false)).BuildAsync(guard.Token))
        {
            var database = await engine.OpenDatabaseAsync("typed", guard.Token);
            await using var seed = await database.CreateSessionAsync(guard.Token);
            for (int first = 0; first < 4096; first += 64)
            {
                await seed.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', Enumerable.Range(first, 64)
                    .Select(id => $"({id},'{new string('x', 4000)}')")), cancellationToken: guard.Token);
            }
            database.Coordinator.Checkpoint();
            SqlPlanExecutor.RewriteStage = (phase, _) =>
            {
                if (phase == "Copied") { brackets++; }
                if (phase == "CopyCheckpointed" && checkpoints++ == 0) { images.Add(storage.CaptureDurableImages()); }
            };
            database.SchemaProvisioner.JointStage = phase =>
            { if (phase == "TablePrepared") { images.Add(storage.CaptureDurableImages()); } };
            try
            {
                // Act
                await database.SchemaProvisioner.ApplyAsync(DeferredCheckpointDeclaration(true).Compile(), guard.Token, deferDataScalingSteps: true);
                await WaitDoneAsync(database, guard.Token);
            }
            finally { SqlPlanExecutor.RewriteStage = null; }
        }
        // Assert
        brackets.ShouldBe(64);
        checkpoints.ShouldBeGreaterThan(0);
        images.Count.ShouldBe(2);
        foreach (var image in images)
        {
            for (int mode = 0; mode < 4; mode++)
            {
                var builder = Builder(image.CaptureDurableImages());
                if (mode != 0) { builder.AddDatabase(DeferredCheckpointDeclaration(mode == 1, mode == 2)); }
                int thirdJobs = 0;
                if (mode == 2)
                {
                    builder.AddWorker(engine =>
                    {
                        engine.BeforeProvisioning = current => current.SchemaProvisioner.JointStage = phase =>
                        { if (phase == "IntentDurable") { thirdJobs++; } };
                        return new RecordingWorker(engine);
                    });
                }
                await using var engine = await builder.BuildAsync(guard.Token);
                var database = await engine.OpenDatabaseAsync("typed", guard.Token);
                await WaitDoneAsync(database, guard.Token);
                if (mode == 2) { thirdJobs.ShouldBe(1); }
                database.Catalog.SchemaState!.ContentHash.ShouldBe(DeferredCheckpointDeclaration(mode == 1, mode == 2).Compile().Hash);
                database.Catalog.PendingMigration.ShouldBeNull();
                database.Provisioning.ShouldBeNull();
                await using var reader = await database.CreateSessionAsync(guard.Token);
                (await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(4096L);
                await DrainReclamationAsync(database, guard.Token);
                database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
            }
        }
        _output.WriteLine($"Checkpoint corpus: brackets={brackets}; checkpoints={checkpoints}; images={images.Count}; reopens={images.Count * 4}");
    }
}
