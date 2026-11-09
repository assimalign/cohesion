using System;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Ad-hoc DROP INDEX resumes its exact intent after two reopens (#1808)")]
    public async Task OpenAsync_CommittedIndexDrop_ShouldReclaimOnlyDroppedOwner()
    {
        // Arrange: the pin prevents the live worker from completing the intent before capture.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE items(v INT)", cancellationToken: guard.Token);
        SeedRaw(database, [1], 256);
        await writer.ExecuteAsync("CREATE INDEX kept ON items(v)", cancellationToken: guard.Token);
        await writer.ExecuteAsync("CREATE INDEX lost ON items(v)", cancellationToken: guard.Token);
        var kept = database.IndexManager.ExportRegistrations().Single(item => item.Definition.Name == "kept");
        var lost = database.IndexManager.ExportRegistrations().Single(item => item.Definition.Name == "lost");
        ulong owner = database.IndexManager.PageOwner(lost), survivor = database.IndexManager.PageOwner(kept);
        var pages = database.DataStorage.GetOwnerIndexPages(survivor).ToArray();
        var pin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);

        // Act.
        await writer.ExecuteAsync("DROP INDEX lost ON items", cancellationToken: guard.Token);
        database.Catalog.GetPendingTableReclamations().ShouldBe([owner]);
        var image = storage.CaptureDurableImages();
        await database.Coordinator.RollbackAsync(pin, guard.Token);

        // Assert.
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var currentStorage = image.CaptureDurableImages();
            await using var recovered = await Builder(currentStorage).BuildAsync(guard.Token);
            var current = await recovered.OpenDatabaseAsync("typed", guard.Token);
            await DrainRequestedReclamationAsync(current, guard.Token);
            current.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
            current.DataStorage.GetOwnerIndexPages(survivor).ShouldBe(pages);
            current.Catalog.GetIndexRegistrations().ShouldBe([kept]);
            current.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
            await using var reader = await current.CreateSessionAsync(guard.Token);
            (await RowsAsync(reader, "SELECT COUNT(*) FROM items WHERE v=1", guard.Token)).Single()[0].ShouldBe(256L);
            await ReadSurvivingTreeAsync(current, kept, 256, guard.Token);
            image = currentStorage.CaptureDurableImages();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Unpublished index root and chunk crash images reclaim exact owners on two reopens (#1808)")]
    [InlineData("owner-reserved")]
    [InlineData("root-before-commit")]
    [InlineData("root-committed")]
    [InlineData("chunk-before-commit")]
    [InlineData("chunk-committed")]
    [InlineData("publication-before")]
    [InlineData("publication-after")]
    public async Task OpenAsync_UnpublishedIndexBoundary_ShouldReclaimOwnerAndKeepHeap(string boundary)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var storage = new CrashCaptureSqlStorageStrategy();
        await using var engine = await Builder(storage).BuildAsync(guard.Token);
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE items(v INT)", cancellationToken: guard.Token);
        SeedRaw(database, [1], 130);
        await writer.ExecuteAsync("CREATE INDEX kept ON items(v)", cancellationToken: guard.Token);
        var kept = database.IndexManager.ExportRegistrations().Single();
        ulong keptOwner = database.IndexManager.PageOwner(kept);
        var keptPages = database.DataStorage.GetOwnerIndexPages(keptOwner).ToArray();
        using var lifecycle = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Informational);
        Assimalign.Cohesion.Database.Indexing.BTreeIndexRegistration? built = null;
        Assimalign.Cohesion.Database.Storage.PageId[] builtPages = [];
        CrashCaptureSqlStorageStrategy? image = null;
        ulong owner = 0;
        SqlPlanExecutor.IndexBuildBoundary = (stage, _, _) =>
        {
            if (stage != boundary || image is not null) { return; }
            built = database.IndexManager.ExportRegistrations().SingleOrDefault(item => item.Definition.Name == "ix");
            owner = stage == "owner-reserved" ? database.Catalog.CaptureTableReclamations().Single().ObjectId
                : database.IndexManager.PageOwner(built!);
            builtPages = database.DataStorage.GetOwnerIndexPages(owner).ToArray();
            database.Catalog.CaptureTableReclamations().Count(item => item.ObjectId == owner).ShouldBe(stage == "publication-after" ? 0 : 1);
            database.Catalog.GetIndexRegistrations().Count(item => item.Definition.Name == "ix").ShouldBe(stage == "publication-after" ? 1 : 0);
            database.DataStorage.WriteBackDirtyPages(int.MaxValue);
            database.DataStorage.WriteAheadJournal.Flush(forceDurable: true);
            image = storage.CaptureDurableImages();
            throw new InvalidOperationException("injected build boundary");
        };

        // Act.
        try { await Should.ThrowAsync<InvalidOperationException>(async () => await writer.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: guard.Token)); }
        finally { SqlPlanExecutor.IndexBuildBoundary = null; }

        // Assert: the intent is discoverable after restart and no live tree is lost.
        image.ShouldNotBeNull();
        var stop = lifecycle.Events.Where(item => item.EventId == 29).ShouldHaveSingleItem();
        stop.PayloadNames.ShouldBe(["engineName", "database", "objectId", "succeeded"]);
        stop.Payload![0].ShouldBe(engine.Name.ToString());
        ((bool)stop.Payload[3]!).ShouldBe(boundary == "publication-after");
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var reopenedStorage = image.CaptureDurableImages();
            await using var recovered = await Builder(reopenedStorage).BuildAsync(guard.Token);
            var current = await recovered.OpenDatabaseAsync("typed", guard.Token);
            await DrainRequestedReclamationAsync(current, guard.Token);
            bool published = boundary == "publication-after";
            if (published) { current.DataStorage.GetOwnerIndexPages(owner).ShouldBe(builtPages); }
            else { current.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty(); }
            current.DataStorage.GetOwnerIndexPages(keptOwner).ShouldBe(keptPages);
            current.Catalog.GetIndexRegistrations().OrderBy(item => item.Definition.Name).ShouldBe(
                (published ? new[] { kept, built! } : new[] { kept }).OrderBy(item => item.Definition.Name));
            current.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
            current.IndexManager.ExportRegistrations().Count.ShouldBe(published ? 2 : 1);
            await using var reader = await current.CreateSessionAsync(guard.Token);
            (await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe(130L);
            (await RowsAsync(reader, "SELECT COUNT(*) FROM items WHERE v=1", guard.Token)).Single()[0].ShouldBe(130L);
            await ReadSurvivingTreeAsync(current, kept, 130, guard.Token);
            image = reopenedStorage.CaptureDurableImages();
        }
    }

    private static async Task DrainRequestedReclamationAsync(SqlDatabase database, CancellationToken token)
    {
        // The DDL/abandon path or open must request discovery itself.
        while (database.TableReclamation.HasWork)
        {
            token.ThrowIfCancellationRequested();
            database.TableReclamation.RunBatch(token);
            await Task.Yield();
        }
    }

    private static async Task ReadSurvivingTreeAsync(SqlDatabase database, BTreeIndexRegistration registration, int count, CancellationToken token)
    {
        database.IndexManager.TryGetIndex(registration.ObjectId, registration.Definition.Name, out var tree).ShouldBeTrue();
        var pin = await database.Coordinator.BeginSnapshotPinAsync(token);
        try
        {
            await using var cursor = tree.OpenCursor(pin.Snapshot, IndexKeyRange.All, cancellationToken: token);
            int read = 0;
            while (await cursor.MoveNextAsync(token)) { read++; }
            read.ShouldBe(count);
        }
        finally { await database.Coordinator.RollbackAsync(pin, token); }
    }
}
