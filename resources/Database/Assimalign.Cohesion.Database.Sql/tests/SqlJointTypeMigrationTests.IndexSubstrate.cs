using System;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Testing;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlJointTypeMigrationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Separately owned small trees share the global 64-leaf pruning grant (#1808)")]
    public async Task PruneAsync_ManySmallOwners_ShouldKeepGlobalBoundAndProgress()
    {
        // Arrange.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var engine = SqlDatabaseEngine.Create("index-prune-directory", new()
        { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("typed", guard.Token);
        await using var writer = await database.CreateSessionAsync(guard.Token);
        await writer.ExecuteAsync("CREATE TABLE items(v INT)", cancellationToken: guard.Token);
        await writer.ExecuteAsync("INSERT INTO items VALUES(1)", cancellationToken: guard.Token);
        for (int tree = 0; tree < 70; tree++)
        { await writer.ExecuteAsync($"CREATE INDEX ix_{tree} ON items(v)", cancellationToken: guard.Token); }
        await writer.ExecuteAsync("DELETE FROM items", cancellationToken: guard.Token);
        foreach (var registration in database.IndexManager.ExportRegistrations())
        { database.DataStorage.GetOwnerIndexPageCount(database.IndexManager.PageOwner(registration)).ShouldBe(1); }
        using var admission = TransactionCheckpointInterlock.ForStorage(database.DataStorage).Enter("PruneGrantTest", cancellationToken: guard.Token);

        // Act.
        long first = await database.IndexManager.PruneAsync(database.Coordinator.PruneBound, guard.Token);
        long second = await database.IndexManager.PruneAsync(database.Coordinator.PruneBound, guard.Token);
        long repeat = await database.IndexManager.PruneAsync(database.Coordinator.PruneBound, guard.Token);

        // Assert.
        first.ShouldBe(64);
        second.ShouldBe(6);
        repeat.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - A 10000-page index builds in bounded brackets and DROP preserves the heap and a pinned reader (#1808)")]
    public async Task ExecuteAsync_LargeIndexDrop_ShouldReclaimExactlyItsPagesAfterLastPin()
    {
        // Arrange: the heap exceeds fifty buffer pools; keys approach the encoder limit.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        string path = Path.Combine(Environment.CurrentDirectory, "_out", "ix-s1", Guid.NewGuid().ToString("N"));
        try
        {
            await using var engine = SqlDatabaseEngine.Create("index-substrate", new()
            {
                RootPath = path, BufferPoolCapacity = 1024 * 1024,
                MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1),
                CheckpointJournalSize = 1024 * 1024, JournalSizeLimit = 64 * 1024 * 1024,
            });
            var database = await engine.CreateDatabaseAsync("typed", guard.Token);
            await using var writer = await database.CreateSessionAsync(guard.Token);
            await writer.ExecuteAsync("CREATE TABLE items (v VARCHAR(1000), id INT)", cancellationToken: guard.Token);
            var heap = Table(database);
            const int rows = 80000;
            string prefix = new('x', 980);
            for (int start = 0; start < rows; start += 64)
            {
                using var admission = TransactionCheckpointInterlock.ForStorage(database.DataStorage).Enter("Seed", cancellationToken: guard.Token);
                using var bracket = database.DataStorage.BeginTransaction();
                for (int row = start; row < Math.Min(rows, start + 64); row++)
                {
                    var record = SqlRowCodec.Encode(heap, [prefix + row.ToString("D8", CultureInfo.InvariantCulture), row], TransactionSequence.None);
                    database.DataStorage.InsertRow(bracket, heap.ObjectId, record);
                }
                bracket.Commit();
            }
            var heapPages = database.DataStorage.GetOwnerPages(heap.ObjectId).ToArray();
            (heapPages.LongLength * Page.Size).ShouldBeGreaterThan(50L * engine.EngineOptions.BufferPoolCapacity);
            database.Coordinator.Checkpoint(guard.Token);
            await writer.ExecuteAsync("CREATE INDEX ix_kept ON items(id)", cancellationToken: guard.Token);
            var keptRegistration = database.IndexManager.ExportRegistrations().Single(item => item.Definition.Name == "ix_kept");
            ulong keptOwner = database.IndexManager.PageOwner(keptRegistration);
            var keptPages = database.DataStorage.GetOwnerIndexPages(keptOwner).ToArray();
            database.Coordinator.Checkpoint(guard.Token);
            StorageTransaction? previousBracket = null;
            SqlPlanExecutor.IndexBuildBracketOpened = bracket =>
            {
                if (previousBracket is not null) { previousBracket.IsActive.ShouldBeFalse(); }
                previousBracket = bracket;
            };
            int chunks = 0;
            int largest = 0;
            long journalPeak = 0;
            SqlPlanExecutor.IndexBuildBoundary = (stage, count, length) =>
            {
                journalPeak = Math.Max(journalPeak, length);
                if (stage != "chunk-committed") { return; }
                previousBracket.ShouldNotBeNull().IsActive.ShouldBeFalse();
                chunks++;
                largest = Math.Max(largest, count);
            };
            using var events = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Informational);
            using var journalEvents = new EventSourceRecorder(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Storage"), EventLevel.Informational);
            try { await writer.ExecuteAsync("CREATE INDEX ix_v ON items(v)", cancellationToken: guard.Token); }
            finally { SqlPlanExecutor.IndexBuildBoundary = null; SqlPlanExecutor.IndexBuildBracketOpened = null; }
            var registration = database.IndexManager.ExportRegistrations().Single(item => item.Definition.Name == "ix_v");
            ulong owner = database.IndexManager.PageOwner(registration);
            var treePages = database.DataStorage.GetOwnerIndexPages(owner).ToArray();
            long checkpointPeak = journalEvents.Events.Where(item => item.EventId == 5 && Equals(item.Payload![0], database.DataStorage.Name.ToString()))
                .Select(item => (long)item.Payload![2]!).DefaultIfEmpty().Max();
            checkpointPeak.ShouldBeGreaterThan(0);
            journalPeak = Math.Max(journalPeak, checkpointPeak);
            int height = SqlSchemaProvisioner.JointTreeHeight(database.DataStorage, registration.RootPageId, owner, out _);
            long chunkBound = SqlSchemaProvisioner.JointBracketBytes(64L * (4 * height + 3), 64L * 2 * height);
            journalPeak.ShouldBeLessThanOrEqualTo(engine.EngineOptions.CheckpointJournalSize + chunkBound,
                "the due-checkpoint journal can grow by at most the currently admitted chunk");
            owner.ShouldNotBe(heap.ObjectId);
            treePages.Length.ShouldBeGreaterThanOrEqualTo(10000);
            chunks.ShouldBe((rows + 63) / 64);
            largest.ShouldBe(64);
            await using var reader = await database.CreateSessionAsync(guard.Token);
            await using var snapshot = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, guard.Token);
            long reclaimed = database.TableReclamation.ReclaimedPages;
            database.IndexManager.TryGetIndex(heap.ObjectId, "ix_v", out var detached).ShouldBeTrue();

            // Act: a snapshot and its detached physical tree remain readable through DROP.
            await writer.ExecuteAsync("DROP INDEX ix_v ON items", cancellationToken: guard.Token);
            database.TableReclamation.Request();
            database.TableReclamation.RunBatch(guard.Token);
            database.DataStorage.GetOwnerIndexPages(owner).ShouldBe(treePages);
            await using (var cursor = detached.OpenCursor(snapshot.Context.Snapshot, IndexKeyRange.All, cancellationToken: guard.Token))
            { (await cursor.MoveNextAsync(guard.Token)).ShouldBeTrue(); }
            (await RowsAsync(reader, "SELECT COUNT(*) FROM items", guard.Token)).Single()[0].ShouldBe((long)rows);
            await snapshot.RollbackAsync(guard.Token);
            await DrainReclamationAsync(database, guard.Token);

            // Assert: the exact owner set returns, while the heap is unchanged.
            database.DataStorage.GetOwnerIndexPages(owner).ShouldBeEmpty();
            database.DataStorage.GetOwnerPages(heap.ObjectId).ShouldBe(heapPages);
            (database.TableReclamation.ReclaimedPages - reclaimed).ShouldBe(treePages.LongLength);
            database.Catalog.GetIndexRegistrations().ShouldHaveSingleItem().ShouldBe(keptRegistration);
            database.DataStorage.GetOwnerIndexPages(keptOwner).ShouldBe(keptPages);
            var keptRows = await RowsAsync(writer, "SELECT id FROM items WHERE id >= 0 ORDER BY id", guard.Token);
            writer.LastStatementMetrics!.AccessPath.ShouldBeOneOf("seek:ix_kept", "ordered:ix_kept:forward");
            keptRows.Length.ShouldBe(rows);
            for (int row = 0; row < rows; row++) { keptRows[row][0].ShouldBe(row); }
            database.Catalog.GetPendingTableReclamations().ShouldBeEmpty();
            events.Events.ShouldNotContain(item => item.EventId == 0);
            events.Events.Where(item => item.EventId == 28).ShouldHaveSingleItem().Payload![1].ShouldBe(unchecked((long)owner));
            events.Events.Count(item => item.EventId == 29).ShouldBe(1);
            events.Events.Where(item => item.EventId == 30).Sum(item => (int)item.Payload![2]!).ShouldBe(treePages.Length);
            _output.WriteLine($"rows={rows}; heap pages={heapPages.Length}; index pages={treePages.Length}; chunks={chunks}; maximum rows/bracket={largest}; journal peak={journalPeak}");
        }
        finally
        {
            SqlPlanExecutor.IndexBuildBoundary = null;
            SqlPlanExecutor.IndexBuildBracketOpened = null;
            if (Directory.Exists(path)) { Directory.Delete(path, true); }
        }
    }
}
