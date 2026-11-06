using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    // Accessors keep the new tests compilable against #1778's pre-fix assembly.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetOwnerIndexPages")]
    private static extern IReadOnlyList<PageId> OwnerDirectory(global::Assimalign.Cohesion.Database.Storage.Storage storage, ulong owner);

    private const string directoryPoolType = "Assimalign.Cohesion.Database.Storage.Internal.StorageBufferPool, Assimalign.Cohesion.Database.Storage";

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_BufferPool")]
    [return: UnsafeAccessorType(directoryPoolType)]
    private static extern object DirectoryPool(global::Assimalign.Cohesion.Database.Storage.Storage storage);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Evict")]
    private static extern void EvictDirectoryPage([UnsafeAccessorType(directoryPoolType)] object pool, PageId page, StorageStream stream);

    private static long[] ScanDirectoryPages(SqlDatabase db, ulong owner)
    {
        var pages = new List<long>();
        for (long page = 1; page < db.DataStorage.PageManager.PageCount; page++)
        {
            if (!db.DataStorage.FreeSpaceMap.IsAllocated((PageId)page)) { continue; }
            using var handle = db.DataStorage.PageManager.GetPage((PageId)page);
            if (handle.Page.Type == PageType.Index && handle.Page.OwnerId == owner) { pages.Add(page); }
        }
        return pages.ToArray();
    }

    private static async Task<(CrashCaptureSqlStorageStrategy Image, ulong Owner, long[] Pages)> SeedDirectoryRetirementAsync(bool crash, CancellationToken token)
    {
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = Builder(storage);
        builder.Options.PageWriteBackInterval = TimeSpan.FromHours(1);
        builder.Options.MaintenanceInterval = TimeSpan.FromHours(1);
        builder.Options.CheckpointInterval = TimeSpan.MaxValue; builder.Options.CheckpointJournalSize = 0;
        await using var engine = await builder.AddDatabase(RetirementDeclaration(true)).BuildAsync(token);
        var db = await engine.OpenDatabaseAsync("typed", token);
        ulong owner = Table(db).ObjectId;
        await using var writer = await db.CreateSessionAsync(token);
        for (int start = 1; start <= 256; start += 64)
        {
            await writer.ExecuteAsync("INSERT INTO items VALUES " + string.Join(',', Enumerable.Range(start, 64).Select(id =>
                $"({id},'{id:D4}{new string('a', 396)}','{id:D4}{new string('b', 396)}')")), cancellationToken: token);
        }
        long[] pages = ScanDirectoryPages(db, owner);
        pages.Length.ShouldBeGreaterThan(10);
        await db.SchemaProvisioner.ApplyAsync(RetirementDeclaration(false).Compile(), token);
        db.IndexManager.ExportRegistrations().Count.ShouldBe(1);
        ScanDirectoryPages(db, owner).ShouldBe(pages);
        if (crash)
        {
            // The image is captured before disposal/checkpoint. These index headers
            // exist only in committed WAL, so open must recover them before indexing.
            db.DataStorage.JournalLength.ShouldBeGreaterThan(0);
            byte[] data = storage.GetDurableImage("typed").Data;
            int stored = 0;
            for (int offset = Page.Size; offset < data.Length; offset += Page.Size)
            {
                if (data[offset + 21] == (byte)PageType.Index) { stored++; }
            }
            stored.ShouldBeLessThan(pages.Length);
        }
        else { db.Coordinator.Checkpoint(token); db.CatalogStorage.Checkpoint(); }
        return (storage.CaptureDurableImages(), owner, pages);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Owner directory: reopened and crash-recovered stranded pages retire at exact volume admission (#1778)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApplyAsync_OwnerDirectoryRecoveredStrands_ShouldRetireAtExactCap(bool crash, bool sparse)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var seed = await SeedDirectoryRetirementAsync(crash, guard.Token);
        long need = 0;
        for (int run = 0; run < 3; run++)
        {
            var image = seed.Image.CaptureDurableImages(); image.UnixSparseGrowth = sparse;
            var builder = Builder(image);
            builder.Options.CheckpointInterval = TimeSpan.MaxValue; builder.Options.CheckpointJournalSize = 0;
            await using var engine = await builder.BuildAsync(guard.Token);
            var db = await engine.OpenDatabaseAsync("typed", guard.Token);
            var snapshot = OwnerDirectory(db.DataStorage, seed.Owner);
            snapshot.Select(page => (long)page).ShouldBe(seed.Pages);
            long before = image.AllocatedBytes;
            if (run != 0) { image.VolumeCapacityBytes = before + need - (run == 2 ? Page.Size : 0); }
            int intents = 0;
            db.SchemaProvisioner.JointStage = stage => { if (stage == "IntentDurable") { intents++; } };
            try
            {
                // Act / Assert
                if (run == 2)
                {
                    var refusal = await Should.ThrowAsync<SqlSchemaMigrationException>(async () => await db.ApplySchemaAsync(Compiled(), guard.Token));
                    refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS03402");
                    intents.ShouldBe(0); db.Catalog.PendingMigration.ShouldBeNull();
                    OwnerDirectory(db.DataStorage, seed.Owner).Select(page => (long)page).ShouldBe(seed.Pages);
                }
                else
                {
                    await db.ApplySchemaAsync(Compiled(), guard.Token);
                    if (run == 0) { need = db.SchemaProvisioner.JointSpaceNeed; }
                    db.SchemaProvisioner.JointSpaceNeed.ShouldBe(need);
                    await DrainReclamationAsync(db, guard.Token);
                    OwnerDirectory(db.DataStorage, seed.Owner).ShouldBeEmpty();
                    db.DataStorage.HasOwnerPages(seed.Owner).ShouldBeFalse();
                    snapshot.Select(page => (long)page).ShouldBe(seed.Pages);
                    db.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
                    long free = db.DataStorage.PageManager.FreePageCount;
                    await DrainReclamationAsync(db, guard.Token);
                    db.DataStorage.PageManager.FreePageCount.ShouldBe(free);
                    _output.WriteLine($"directory recovery crash={crash} sparse={sparse} run={run}: N={need}; indexes={seed.Pages.Length}; growth={image.AllocatedBytes - before}; peak={image.PeakAllocatedBytes}; cap={image.VolumeCapacityBytes}");
                }
                image.CapacityFailures.ShouldBe(0); db.IsOffline.ShouldBeFalse();
            }
            finally { image.VolumeCapacityBytes = long.MaxValue; }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Owner directory: DROP admission visits only the retiring owner's index pages as an untouched table grows (#1778)")]
    public async Task BuildAsync_OwnerDirectoryUntouchedGrowth_ShouldKeepAdmissionVisitsConstant()
    {
        // Arrange: the small table has two stranded trees and one registered tree.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var seed = await SeedDirectoryRetirementAsync(false, guard.Token);
        var samples = new List<long[]>();
        foreach (int rows in new[] { 512, 8192 })
        {
            var image = seed.Image.CaptureDurableImages();
            await using (var populate = await Builder(image).BuildAsync(guard.Token))
            {
                var db = await populate.OpenDatabaseAsync("typed", guard.Token);
                await using var session = await db.CreateSessionAsync(guard.Token);
                await session.ExecuteAsync("CREATE TABLE untouched(id INT PRIMARY KEY, payload VARCHAR(1000))", cancellationToken: guard.Token);
                for (int start = 1; start <= rows; start += 64)
                {
                    await session.ExecuteAsync("INSERT INTO untouched VALUES " + string.Join(',', Enumerable.Range(start, 64).Select(id =>
                        $"({id},'{new string('x', 1000)}')")), cancellationToken: guard.Token);
                }
                db.Coordinator.Checkpoint(guard.Token); db.CatalogStorage.Checkpoint();
                image = image.CaptureDurableImages();
            }
            var visits = new List<long>();
            long untouchedPages = 0;
            int admissions = 0;
            var builder = Builder(image);
            builder.Options.PageWriteBackInterval = TimeSpan.FromHours(1);
            builder.Options.MaintenanceInterval = TimeSpan.FromHours(1);
            builder.Options.CheckpointInterval = TimeSpan.MaxValue; builder.Options.CheckpointJournalSize = 0;
            builder.AddWorker(engine =>
            {
                engine.BeforeProvisioning = db =>
                {
                    untouchedPages = db.DataStorage.GetOwnerPages(db.Catalog.Tables.Single(table => table.Name == "untouched").ObjectId).Count;
                    db.SchemaProvisioner.JointSpaceCounting = () =>
                    {
                        admissions++;
                        // Every admission pin must miss: eviction flushes before the
                        // observer is armed, so read identities count all visited pages.
                        object pool = DirectoryPool(db.DataStorage);
                        for (long page = 1; page < db.DataStorage.PageManager.PageCount; page++)
                        { EvictDirectoryPage(pool, (PageId)page, db.DataStorage.Data); }
                        int admissionThread = Environment.CurrentManagedThreadId;
                        image.ObserveDataReads("typed", (position, count) =>
                        {
                            if (Environment.CurrentManagedThreadId != admissionThread) { return; }
                            (position % Page.Size).ShouldBe(0); count.ShouldBe(Page.Size);
                            visits.Add(position / Page.Size);
                        });
                    };
                    db.SchemaProvisioner.JointStage = stage =>
                    { if (stage == "IntentDurable") { image.ObserveDataReads("typed", null); } };
                };
                return new RecordingWorker(engine);
            });
            builder.AddDatabase("typed", database => database.Schema(SqlSchema.Create("typed", schema => schema.AllowDestructiveChanges())));

            // Act: a declared cheap DROP runs admission on Build's inline path.
            await using var engine = await builder.BuildAsync(guard.Token);
            var current = await engine.OpenDatabaseAsync("typed", guard.Token);

            // Assert
            admissions.ShouldBe(1);
            samples.Add(visits.ToArray());
            untouchedPages.ShouldBeGreaterThan(rows / 10);
            current.Catalog.Tables.ShouldNotContain(table => table.Name == "items");
            await using var reader = await current.CreateSessionAsync(guard.Token);
            (await RowsAsync(reader, "SELECT COUNT(*) FROM untouched", guard.Token)).Single()[0].ShouldBe((long)rows);
            _output.WriteLine($"directory admission: untouched rows={rows}, heap pages={untouchedPages}; visits={visits.Count}; retiring index pages={seed.Pages.Length}; foreign visits={visits.Count(page => !seed.Pages.Contains(page))}");
        }
        foreach (var sample in samples) { sample.Order().ShouldBe(seed.Pages); }
        samples[1].Length.ShouldBe(samples[0].Length);
    }
}
