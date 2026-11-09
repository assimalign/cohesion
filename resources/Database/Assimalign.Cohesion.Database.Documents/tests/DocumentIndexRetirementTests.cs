using System;
using System.Linq;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Documents.Tests;

[Collection("Document index concurrency")]
public sealed class DocumentIndexRetirementTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - DROP INDEX retains a pinned tree and reclaims exactly its pages (#1808)")]
    public async Task DeleteIndexAsync_PinnedSnapshot_ShouldDelayExactOwnerReclamation()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = DocumentDatabaseEngine.Create("index-retirement", new()
        { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("test", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        var collection = await writer.CreateCollectionAsync("items", guard.Token);
        string prefix = new('a', 300);
        for (int row = 0; row < 1024; row++)
        { await collection.PutAsync(writer, row.ToString(), Encoding.UTF8.GetBytes($"{{\"v\":\"{prefix}{row:D4}\"}}"), cancellationToken: guard.Token); }
        using var events = new EventSourceRecorder(Assimalign.Cohesion.Database.Documents.Internal.DocumentDatabaseEventSource.Log, EventLevel.Informational);
        await writer.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: guard.Token);
        var pin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        var metadata = database.Catalog.GetIndexes(database.Catalog.FindCollection("items", pin.Snapshot)!.Value.Id, pin.Snapshot).Single();
        var pages = database.DataStorage.GetOwnerIndexPages(metadata.ObjectId).ToArray();
        pages.Length.ShouldBeGreaterThan(64);
        long reclaimed = database.Catalog.IndexOwnerPagesReclaimed;

        // Act.
        await writer.ExecuteAsync("DROP INDEX ix ON items", cancellationToken: guard.Token);
        int reads = 0, passes = 0, overlaps = 0, reading = 0;
        using var started = new ManualResetEventSlim();
        var race = Stopwatch.StartNew();
        var readTask = Task.Run(async () =>
        {
            Volatile.Write(ref reading, 1); started.Set();
            do
            {
                (await database.Catalog.SearchIndexAsync(metadata.CollectionId, "ix", prefix + "0000", true,
                    prefix + "0000", true, pin.Snapshot, guard.Token)).ShouldHaveSingleItem();
                Interlocked.Increment(ref reads);
            } while (race.Elapsed < TimeSpan.FromSeconds(1) || Volatile.Read(ref overlaps) == 0);
            Volatile.Write(ref reading, 0);
        }, guard.Token);
        var reclaimTask = Task.Run(() =>
        {
            started.Wait(guard.Token);
            do
            {
                guard.Token.ThrowIfCancellationRequested();
                database.Coordinator.RunVersionPurgePass(guard.Token);
                if (Volatile.Read(ref reading) != 0) { Interlocked.Increment(ref overlaps); }
                Interlocked.Increment(ref passes);
            } while (race.Elapsed < TimeSpan.FromSeconds(1) || Volatile.Read(ref reading) != 0);
        }, guard.Token);
        await Task.WhenAll(readTask, reclaimTask).WaitAsync(guard.Token);
        reads.ShouldBeGreaterThan(0); passes.ShouldBeGreaterThan(0); overlaps.ShouldBeGreaterThan(0);
        database.Coordinator.RunVersionPurgePass(guard.Token);
        database.DataStorage.GetOwnerIndexPages(metadata.ObjectId).ShouldBe(pages);
        (await database.Catalog.SearchIndexAsync(database.Catalog.FindCollection("items", pin.Snapshot)!.Value.Id, "ix", prefix + "0000", true,
            prefix + "0000", true, pin.Snapshot, guard.Token)).ShouldHaveSingleItem();
        await database.Coordinator.RollbackAsync(pin, guard.Token);
        for (int pass = 0; pass < 32 && (database.DataStorage.GetOwnerIndexPageCount(metadata.ObjectId) != 0); pass++)
        { database.Coordinator.RunVersionPurgePass(guard.Token); }

        // Assert.
        database.DataStorage.GetOwnerIndexPages(metadata.ObjectId).ShouldBeEmpty($"owner={metadata.ObjectId}, bound={database.Coordinator.PruneBound}, checkpoint={database.Catalog.ReclamationCheckpointDue?.Invoke()}, journal={database.DataStorage.Journal.Length}, reclaimed={database.Catalog.IndexOwnerPagesReclaimed - reclaimed}");
        (database.Catalog.IndexOwnerPagesReclaimed - reclaimed).ShouldBe(pages.LongLength);
        (await collection.GetAsync(writer, "0", cancellationToken: guard.Token)).ShouldNotBeNull();
        database.IsOffline.ShouldBeFalse();
        events.Events.ShouldNotContain(item => item.EventId == 0);
        events.Events.Count(item => item.EventId == 15).ShouldBe(1);
        events.Events.Single(item => item.EventId == 15).EventName.ShouldBe("IndexBuildBegan");
        events.Events.Single(item => item.EventId == 15).Opcode.ShouldBe(EventOpcode.Info);
        events.Events.Count(item => item.EventId == 16).ShouldBe(1);
        events.Events.Single(item => item.EventId == 16).EventName.ShouldBe("IndexBuildEnded");
        events.Events.Single(item => item.EventId == 16).Opcode.ShouldBe(EventOpcode.Info);
        events.Events.Where(item => item.EventId == 17).Sum(item => (int)item.Payload![2]!).ShouldBe(pages.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Every durable build and retirement boundary converges after two reopens (#1808)")]
    [InlineData("build-root")]
    [InlineData("build-chunk")]
    [InlineData("build-published")]
    [InlineData("retire-registration")]
    [InlineData("reclaim-pages")]
    [InlineData("reclaim-batch")]
    [InlineData("reclaim-registration")]
    [InlineData("reclaimed")]
    public async Task OpenAsync_CrashBoundary_ShouldConvergeOwnersAndRegistrations(string boundary)
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        await using var engine = DocumentDatabaseEngine.Create("index-crash", new()
        {
            StorageStrategy = strategy, MaintenanceInterval = TimeSpan.FromHours(1),
            CheckpointInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("source", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        var collection = await writer.CreateCollectionAsync("items", guard.Token);
        for (int row = 0; row < 130; row++)
        { await collection.PutAsync(writer, row.ToString(), Encoding.UTF8.GetBytes($"{{\"v\":{row}}}"), cancellationToken: guard.Token); }
        await writer.ExecuteAsync("CREATE INDEX kept ON items(v)", cancellationToken: guard.Token);
        var metadataPin = await database.Coordinator.BeginSnapshotPinAsync(guard.Token);
        Guid collectionId = database.Catalog.FindCollection("items", metadataPin.Snapshot)!.Value.Id;
        var kept = database.Catalog.GetIndexes(collectionId, metadataPin.Snapshot).Single();
        var keptPages = database.DataStorage.GetOwnerIndexPages(kept.ObjectId).ToArray();
        await database.Coordinator.RollbackAsync(metadataPin, guard.Token);
        bool build = boundary.StartsWith("build", StringComparison.Ordinal);
        if (!build)
        {
            await writer.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: guard.Token);
            await writer.ExecuteAsync("DROP INDEX ix ON items", cancellationToken: guard.Token);
        }
        int captures = 0;
        database.Catalog.AfterIndexRetirementMutation = stage =>
        {
            if (stage != boundary) { return; }
            database.DataStorage.WriteBackDirtyPages(int.MaxValue);
            database.DataStorage.WriteAheadJournal.Flush(forceDurable: true);
            strategy.CopyDatabase("source", "image");
            captures++;
            throw new InvalidOperationException("injected index boundary");
        };

        // Act.
        try
        {
            if (build) { await Should.ThrowAsync<InvalidOperationException>(async () => await writer.ExecuteAsync("CREATE INDEX ix ON items(v)", cancellationToken: guard.Token)); }
            else
            {
                for (int pass = 0; pass < 8 && captures == 0; pass++)
                {
                    try { database.Coordinator.RunVersionPurgePass(guard.Token); }
                    catch (InvalidOperationException) when (captures == 1) { }
                }
            }
        }
        finally { database.Catalog.AfterIndexRetirementMutation = null; }

        // Assert: all abandoned/dropped owners and registrations disappear; content survives.
        captures.ShouldBe(1);
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var image = await engine.OpenDatabaseAsync("image", guard.Token);
            for (int pass = 0; pass < 16; pass++) { image.Coordinator.RunVersionPurgePass(guard.Token); }
            await using var reader = await image.CreateSessionAsync(guard.Token);
            var items = await reader.GetCollectionAsync("items", guard.Token);
            (await items.GetAsync(reader, "129", cancellationToken: guard.Token)).ShouldNotBeNull();
            var pin = await image.Coordinator.BeginSnapshotPinAsync(guard.Token);
            image.Catalog.GetIndexes(collectionId, pin.Snapshot).ShouldBe([kept]);
            (await image.Catalog.SearchIndexAsync(collectionId, "kept", 129m, true, 129m, true, pin.Snapshot, guard.Token)).ShouldHaveSingleItem();
            await image.Coordinator.RollbackAsync(pin, guard.Token);
            using (var registrations = image.DataStorage.GetUnitIterator(1))
            {
                registrations.MoveNext().ShouldBeTrue(); registrations.MoveNext().ShouldBeFalse();
            }
            image.DataStorage.GetIndexOwners(0).Select(owner => owner.OwnerId).ShouldBe([kept.ObjectId]);
            image.DataStorage.GetOwnerIndexPages(kept.ObjectId).ShouldBe(keptPages);
            await reader.DisposeAsync();
            await image.DisposeAsync();
        }
    }
}
