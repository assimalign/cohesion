using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphTextIndexRecoveryTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Text index crash boundaries converge on two reopens (#1644)")]
    [InlineData("label")] [InlineData("catalog")] [InlineData("property")] [InlineData("tree")]
    [InlineData("commit-record")] [InlineData("committed")]
    [InlineData("drop-catalog")] [InlineData("drop-tree")] [InlineData("committed-drop")]
    [InlineData("build-root")] [InlineData("build-chunk")] [InlineData("build-published")]
    [InlineData("update")] [InlineData("committed-update")] [InlineData("delete")]
    [InlineData("retire-registration")] [InlineData("reclaim-pages")] [InlineData("reclaim-registration")] [InlineData("reclaimed")]
    [InlineData("reclaim-batch")] [InlineData("reclaim-batch-large")]
    [InlineData("retire-registration", false)] [InlineData("reclaim-pages", false)]
    [InlineData("reclaim-batch", false)] [InlineData("reclaim-registration", false)] [InlineData("reclaimed", false)]
    public async Task Execute_CrashImage_ShouldConvergeToExactCommittedStateAsync(string boundary, bool text = true)
    {
        // Arrange: a durable fault-injecting storage captures actual WAL/page images, not copies of model objects.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = GraphTextIndexTests.Quiet();
        options.StorageStrategy = strategy;
        await using var engine = GraphDatabaseEngine.Create("text-crash", options);
        var database = await engine.CreateDatabaseAsync("source", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.ExecuteAsync("INSERT (:N {k:1,p:'oldabc'}),(:N {k:2,p:'abcXbcd'})", cancellationToken: token);
        var initialOwners = GraphIndexReclamationReviewTests.IndexOwners(database).Keys.ToArray();
        var initialRegistrations = GraphTextIndexTests.Registrations(database).Select(entry => entry.Id).ToArray();
        ulong imageTree = 0;
        string build = text ? "CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)" : "CREATE INDEX text_p FOR (n:N) ON (n.p)";
        bool reclamation = boundary.StartsWith("reclaim", StringComparison.Ordinal) || boundary == "retire-registration";
        bool drop = boundary.Contains("drop", StringComparison.Ordinal) || reclamation;
        bool update = boundary is "update" or "committed-update" or "delete";
        if (boundary == "reclaim-batch-large")
        {
            string value = GraphTextIndexTests.UniqueAscii(4096) + GraphTextIndexTests.UniqueAscii(2800).ToUpperInvariant();
            value = "!" + string.Concat(value.Select(character => character is >= 'a' and <= 'p' ? (char)(character + 10) : character)) + "!";
            await session.ExecuteAsync("FOR k IN [3,4] INSERT (:N {k:k,p:$pattern})", GraphTextIndexTests.Bind(value), token);
        }
        if (drop || update) { await session.ExecuteAsync(build, cancellationToken: token); }
        ulong largeTree = 0;
        long largeRoot = 0;
        if (boundary == "reclaim-batch-large")
        {
            var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
            try { largeTree = database.Store.GetIndexIdentities(context.Snapshot).Single().Id; }
            finally { await database.Coordinator.RollbackAsync(context, token); }
            largeRoot = GraphTextIndexTests.ReadIndexRoot(database, largeTree);
            database.DataStorage.GetOwnerIndexPages(largeTree).Count.ShouldBeGreaterThan(Graph.Storage.GraphStore.IndexReclaimPageLimit);
        }
        if (drop || update) { CaptureIdentity(); }
        if (reclamation)
        { await session.ExecuteAsync("DROP INDEX text_p", cancellationToken: token); }
        int captured = 0;
        bool buildPublished = false;
        database.Store.AfterTextIndexMutation = stage =>
        {
            if (stage == "build-published") { buildPublished = true; }
            if (stage != (boundary == "reclaim-batch-large" ? "reclaim-batch" : boundary)) { return; }
            if (boundary == "reclaim-batch-large")
            {
                var remaining = database.DataStorage.GetOwnerIndexPages(largeTree);
                remaining.Count.ShouldBeGreaterThan(0);
                remaining.ShouldNotContain((PageId)largeRoot);
            }
            Capture();
            throw new InvalidOperationException("Abandon text index at " + stage);
        };
        if (boundary is "update" or "delete")
        {
            database.Store.AfterApply = () =>
            {
                Capture();
                throw new InvalidOperationException("Abandon node mutation after postings");
            };
        }

        // Act
        try
        {
            if (boundary == "commit-record")
            {
                FaultInjectingJournalStorageStrategy.BeforeCommitFlush = () => { if (!buildPublished) { return; } CaptureIdentity(); strategy.CopyDatabase("source", "image"); captured++; };
                await session.ExecuteAsync(build, cancellationToken: token);
            }
            else if (boundary is "committed" or "committed-drop" or "committed-update")
            {
                await session.ExecuteAsync(boundary == "committed" ? build
                    : boundary == "committed-drop" ? "DROP INDEX text_p" : "MATCH (n:N {k:1}) SET n.p='newxyz'", cancellationToken: token);
                Capture();
            }
            else if (reclamation)
            {
                for (int pass = 0; captured == 0 && pass < 4; pass++)
                {
                    if (boundary == "reclaim-registration" || boundary == "reclaimed")
                    {
                        try { database.Coordinator.RunVersionPurgePass(token); }
                        catch (InvalidOperationException) when (captured == 1) { }
                    }
                    else { Should.Throw<InvalidOperationException>(() => database.Coordinator.RunVersionPurgePass(token)); }
                }
            }
            else
            {
                await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(drop ? "DROP INDEX text_p"
                    : boundary == "update" ? "MATCH (n:N {k:1}) SET n.p='newxyz'"
                    : boundary == "delete" ? "MATCH (n:N {k:1}) DELETE n"
                    : build, cancellationToken: token));
            }
        }
        finally
        {
            database.Store.AfterApply = null;
            database.Store.AfterTextIndexMutation = null;
            FaultInjectingJournalStorageStrategy.BeforeCommitFlush = null;
        }

        // Assert: complete metadata and exact stored values survive; an abandoned writer never leaves false postings.
        captured.ShouldBe(1);
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var recovered = await engine.OpenDatabaseAsync("image", token);
            await using var reader = await recovered.CreateSessionAsync(token);
            bool indexed = boundary is "commit-record" or "committed" or "drop-catalog" or "drop-tree" or "update" or "committed-update" or "delete";
            var indexes = await GraphSchema.Open(recovered, reader).GetIndexesAsync(token);
            indexes.Count.ShouldBe(indexed ? 1 : 0);
            if (indexed) { indexes[0].IsText.ShouldBe(text); }
            (await GraphTextIndexTests.RowsAsync(reader, GraphTextIndexTests.Query("CONTAINS"), "abc")).Select(row => row[0])
                .ShouldBe(boundary == "committed-update" ? [2L] : new object?[] { 1L, 2L });
            (await GraphTextIndexTests.RowsAsync(reader, GraphTextIndexTests.Query("ENDS WITH"), "xyz")).Select(row => row[0])
                .ShouldBe(boundary == "committed-update" ? [1L] : Array.Empty<object?>());
            (await GraphTextIndexTests.RowsAsync(reader, GraphTextIndexTests.Query("CONTAINS"), "abcd")).ShouldBeEmpty();
            for (int pass = 0; pass < 16; pass++) { recovered.Coordinator.RunVersionPurgePass(token); }
            var registrations = GraphTextIndexTests.Registrations(recovered);
            registrations.Select(entry => entry.Id).Order().ShouldBe(initialRegistrations.Concat(indexed ? [imageTree] : Array.Empty<ulong>()).Order());
            registrations.ShouldAllBe(entry => entry.Root > 0);
            if (indexed)
            {
                string[] values = boundary == "committed-update" ? ["newxyz", "abcXbcd"] : ["oldabc", "abcXbcd"];
                var expected = values.SelectMany(value => Internal.GraphTextTrigrams.Keys(value, token))
                    .GroupBy(key => Convert.ToHexString(key.Encoded.Span)).ToDictionary(group => group.Key, group => group.Count());
                var postings = await GraphTextIndexTests.CountEntriesByTokenAsync(recovered, imageTree);
                postings.OrderBy(pair => pair.Key).ShouldBe(expected.OrderBy(pair => pair.Key));
                (await GraphTextIndexTests.CountEntriesAsync(recovered, imageTree)).ShouldBe(expected.Values.Sum());
            }
            else
            {
                if (imageTree != 0) { recovered.DataStorage.GetOwnerIndexPages(imageTree).ShouldBeEmpty(); }
                GraphIndexReclamationReviewTests.IndexOwners(recovered).Keys.Order().ShouldBe(initialOwners.Order());
            }
            await reader.DisposeAsync();
            await recovered.DisposeAsync();
        }

        void CaptureIdentity()
        {
            ulong id = GraphIndexReclamationReviewTests.IndexOwners(database).Keys.Except(initialOwners).SingleOrDefault();
            if (id != 0) { imageTree = id; }
        }

        void Capture()
        {
            CaptureIdentity();
            database.DataStorage.WriteBackDirtyPages(int.MaxValue);
            database.DataStorage.WriteAheadJournal.Flush(forceDurable: true);
            strategy.CopyDatabase("source", "image");
            captured++;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - A deferred undo cannot reclaim an orphan before its creator releases (#1644)")]
    public async Task Rollback_DeferredDefinitionUndo_ShouldKeepOrphanRootUntilSafeAndReopenAsync()
    {
        // Arrange: the existing caller-owned store API runs under a real kernel savepoint.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = GraphTextIndexTests.Quiet();
        options.StorageStrategy = strategy;
        options.DeferredUndoRetryDelay = TimeSpan.FromHours(1);
        await using var engine = GraphDatabaseEngine.Create("text-deferred-undo", options);
        var database = await engine.CreateDatabaseAsync("source", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.ExecuteAsync("INSERT (:N {k:1,p:'oldabc'})", cancellationToken: token);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        try
        {
            await database.Coordinator.CreateSavepointAsync(context, token);
            await database.Store.CreateIndexAsync("N", "p", context, null, null, token, text: true);
            ulong id = database.Store.GetIndexIdentities(context.Snapshot).Single().Id;
            int pages = database.DataStorage.GetOwnerIndexPages(id).Count;
            PageId definitionPage = default;
            int definitionSlot = -1;
            using (var records = database.DataStorage.GetUnitIterator(2))
            {
                while (records.MoveNext())
                {
                    var record = records.Current;
                    if (record.Data.Span[16] != 3) { continue; }
                    definitionPage = record.PageId;
                    definitionSlot = record.SlotIndex;
                }
            }
            definitionSlot.ShouldBeGreaterThanOrEqualTo(0);
            database.DataStorage.WriteBackDirtyPages(int.MaxValue);
            database.DataStorage.WriteAheadJournal.Flush(forceDurable: true);

            // Act: directory undo runs first; the physical definition delete then hits a held page.
            using (var holder = database.DataStorage.BeginTransaction())
            {
                using (database.DataStorage.OpenPageForWrite(holder, definitionPage)) { }
                await database.Coordinator.RollbackAsync(context, token);
                database.Coordinator.VersionStore.PendingAbortedPurges.ShouldContain(context.Sequence.Value);
                database.Store.GetIndexes(context.Snapshot).ShouldBeEmpty();
                RecordVersionStamp.ReadStamps(database.DataStorage.ReadEntry(definitionPage, definitionSlot).Span).Writer.ShouldBe(context.Sequence);
                Should.Throw<StorageTransactionException>(() => database.Coordinator.RunVersionPurgePass(token));

                // Assert: the orphan is pinned even without a directory entry; registration and pages survive.
                database.DataStorage.GetOwnerIndexPages(id).Count.ShouldBe(pages);
                GraphTextIndexTests.ReadIndexRoot(database, id).ShouldBeGreaterThan(0);
                database.DataStorage.WriteAheadJournal.Flush(forceDurable: true);
                strategy.CopyDatabase("source", "image");
            }
            database.Coordinator.RunVersionPurgePass(token);
            database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
            (await GraphTextIndexTests.RowsAsync(session, GraphTextIndexTests.Query("CONTAINS"), "abc")).ShouldHaveSingleItem();
            database.Coordinator.RunVersionPurgePass(token);
            database.DataStorage.GetOwnerIndexPages(id).ShouldBeEmpty();
            for (int reopen = 0; reopen < 2; reopen++)
            {
                var recovered = await engine.OpenDatabaseAsync("image", token);
                await using var reader = await recovered.CreateSessionAsync(token);
                (await GraphTextIndexTests.RowsAsync(reader, GraphTextIndexTests.Query("CONTAINS"), "abc")).ShouldHaveSingleItem()[0].ShouldBe(1L);
                (await GraphSchema.Open(recovered, reader).GetIndexesAsync(token)).ShouldBeEmpty();
                recovered.Coordinator.RunVersionPurgePass(token);
                recovered.DataStorage.GetOwnerIndexPages(id).ShouldBeEmpty();
                await reader.DisposeAsync();
                await recovered.DisposeAsync();
            }
        }
        finally
        {
            if (context.State == TransactionState.Active) { await database.Coordinator.RollbackAsync(context, token); }
            database.Coordinator.RunVersionPurgePass(token);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Concurrent populated text build serializes queued writes and backfills every node (#1644)")]
    public async Task Execute_ConcurrentPopulatedBuild_ShouldIncludeQueuedWriterAsync()
    {
        // Arrange: a recorded writer wait decides when the concurrent schedule has been exercised.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("text-build", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", token);
        await using var builder = await database.CreateSessionAsync(token);
        await using var writer = await database.CreateSessionAsync(token);
        await builder.ExecuteAsync("FOR k IN [1,2,3,4,5] INSERT (:N {k:k,p:'oldabc'})", cancellationToken: token);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        database.Store.AfterTextIndexMutation = stage =>
        { if (stage == "tree") { entered.Set(); release.Wait(token); } };
        using var waits = GraphTypeReviewRegressionTests.RecordLockWaits();
        var build = Task.Run(async () => await builder.ExecuteAsync("CREATE TEXT INDEX text_p FOR (n:N) ON (n.p)", cancellationToken: token), token);
        Task<DatabaseResult>? queued = null;
        try
        {
            entered.Wait(token);
            queued = writer.ExecuteAsync("INSERT (:N {k:6,p:'newabc'})", cancellationToken: token).AsTask();
            GraphTypeReviewRegressionTests.AssertWriterWait(waits);
            queued.IsCompleted.ShouldBeFalse();
        }
        finally { release.Set(); }

        // Act
        try { await build; await queued!; }
        finally { database.Store.AfterTextIndexMutation = null; }

        // Assert
        (await GraphTextIndexTests.RowsAsync(builder, GraphTextIndexTests.Query("CONTAINS"), "abc")).Select(row => row[0]).ShouldBe([1L, 2L, 3L, 4L, 5L, 6L]);
        (await GraphTextIndexTests.RowsAsync(builder, "EXPLAIN " + GraphTextIndexTests.Query("CONTAINS"), "abc")).ShouldContain(row => Equals(row[2], "TextIndexSeek"));
    }
}
