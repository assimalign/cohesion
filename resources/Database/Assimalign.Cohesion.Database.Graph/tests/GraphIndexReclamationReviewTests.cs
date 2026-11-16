using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphIndexReclamationReviewTests
{
    private readonly ITestOutputHelper _output;
    public GraphIndexReclamationReviewTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Graph] - Failed statement registry rebuild and retirement cannot interleave (#1644)")]
    public async Task Execute_FailingStatementDuringRetirement_ShouldPreserveOriginalErrorAsync()
    {
        // Arrange: stop the failed statement just after clearing registrations, outside the apply gate.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("registry-race", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", token);
        await using var writer = await database.CreateSessionAsync(token);
        await writer.ExecuteAsync("INSERT (:N {p:$pattern}),(:E {p:'abc'})", GraphTextIndexTests.Bind(GraphTextIndexTests.UniqueAscii(1024)), token);
        await writer.ExecuteAsync("CREATE TEXT INDEX t FOR (n:N) ON (n.p)", cancellationToken: token);
        await writer.ExecuteAsync("CREATE INDEX e FOR (n:E) ON (n.p)", cancellationToken: token);
        var identity = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        ulong retired = database.Store.GetIndexIdentities(identity.Snapshot).Single(index => index.Label == "E").Id;
        await database.Coordinator.RollbackAsync(identity, token);
        await writer.ExecuteAsync("DROP INDEX e", cancellationToken: token);
        using var cleared = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        int rebuilds = 0;
        int attempts = 0;
        database.Store.AfterIndexRegistrationsCleared = () =>
        { if (Interlocked.Increment(ref rebuilds) == 1) { cleared.Set(); release.Wait(token); } };
        database.Store.BeforeIndexReclamation = () => { Interlocked.Increment(ref attempts); attempted.Set(); };
        Exception? maintenanceError = null;
        var maintenance = new Thread(() =>
        {
            try { database.Coordinator.RunVersionPurgePass(token); }
            catch (Exception error) { maintenanceError = error; }
            finally { finished.Set(); }
        });
        var write = Task.Run(async () => await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync(
            GraphQueryRequest.FromGql("MATCH (n:N) SET n.p=$pattern", GraphTextIndexTests.Bind(new string('z', 1024)),
                new() { WorkUnits = 1500 }), token)), token);

        // Act: the dedicated maintenance thread either waits on the registry monitor or exposes the old race.
        try
        {
            cleared.Wait(token);
            maintenance.Start();
            attempted.Wait(token);
            SpinWait.SpinUntil(() => finished.IsSet || (maintenance.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(30)).ShouldBeTrue();
        }
        finally { release.Set(); }
        var error = await write;
        finished.Wait(token);
        maintenance.Join();
        database.Store.AfterIndexRegistrationsCleared = null;
        database.Store.BeforeIndexReclamation = null;

        // Assert: both sides ran; rebuilding must not replace CG05004 with duplicate-key failure.
        _output.WriteLine($"registry interleaving: rebuilds={rebuilds}, retire attempts={attempts}, statement={string.Join(',', error.Diagnostics.Select(d => d.Code))}; owner pages={database.DataStorage.GetOwnerIndexPages(retired).Count}; maintenance={maintenanceError}");
        database.DataStorage.GetOwnerIndexPages(retired).ShouldBeEmpty();
        rebuilds.ShouldBeGreaterThanOrEqualTo(2);
        attempts.ShouldBe(1);
        maintenanceError.ShouldBeNull();
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        (await GraphTextIndexTests.RowsAsync(writer, GraphTextIndexTests.Query("CONTAINS"), "aaa")).ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - A tree page refusing reclamation cannot suppress live pruning or type counts (#1644)")]
    public async Task PruneIndexesAsync_LockedRetiredPage_ShouldRunOtherMaintenanceBeforeRethrowAsync()
    {
        // Arrange: FreeOwnerPages hits an actual conflicting page owner.
        await using var engine = GraphDatabaseEngine.Create("reclaim-failure", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {p:'abc'})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX e FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        ulong id = database.Store.GetIndexIdentities(context.Snapshot).Single().Id;
        await database.Coordinator.RollbackAsync(context, CancellationToken.None);
        var page = database.DataStorage.GetOwnerIndexPages(id).First();
        await session.ExecuteAsync("DROP INDEX e", cancellationToken: CancellationToken.None);
        int prunes = 0, counts = 0;
        database.Store.AfterIndexPrune = () => prunes++;
        database.Store.AfterRelationshipTypeCountRefresh = () => counts++;
        using var holder = database.DataStorage.BeginTransaction();
        using (database.DataStorage.OpenPageForWrite(holder, page)) { }

        // Act / Assert
        try
        {
            Should.Throw<StorageTransactionException>(() => database.Coordinator.RunVersionPurgePass(CancellationToken.None));
            prunes.ShouldBe(1);
            counts.ShouldBe(1);
            database.DataStorage.GetOwnerIndexPages(id).ShouldNotBeEmpty();
        }
        finally { database.Store.AfterIndexPrune = null; database.Store.AfterRelationshipTypeCountRefresh = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Refused backfill allocates no abandoned owner pages (#1644)")]
    [InlineData(false)] [InlineData(true)]
    public async Task CreateIndexAsync_WorkRefusal_ShouldLeaveNoOwnerPagesAfterReopenAsync(bool text)
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = GraphTextIndexTests.Quiet(); options.StorageStrategy = strategy;
        await using var engine = GraphDatabaseEngine.Create("build-refusal", options);
        var database = await engine.CreateDatabaseAsync("source", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // A short exact key permits the same extraction/insertion boundary to be tested.
        await GraphTextIndexReviewTests.PopulateAsync(session, text ? 1 : 10, text ? GraphTextIndexTests.UniqueAscii(1024) : "abc");
        var before = IndexOwners(database);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(GraphQueryRequest.FromGql(
            $"CREATE {(text ? "TEXT " : "")}INDEX t FOR (n:N) ON (n.p)", null, new() { WorkUnits = text ? 1500 : 15 }), CancellationToken.None));
        var after = IndexOwners(database);
        var abandoned = after.Keys.Except(before.Keys).ToArray();
        _output.WriteLine($"refused {(text ? "text" : "exact")} build: {string.Join(',', abandoned.Select(id => $"owner={id}:pages={after[id]}"))}");
        database.DataStorage.WriteBackDirtyPages(int.MaxValue);
        database.DataStorage.WriteAheadJournal.Flush(forceDurable: true);
        strategy.CopyDatabase("source", "image");
        var recovered = await engine.OpenDatabaseAsync("image", CancellationToken.None);
        try
        {
            for (int pass = 0; pass < 8; pass++) { recovered.Coordinator.RunVersionPurgePass(CancellationToken.None); }
            foreach (ulong id in abandoned) { recovered.DataStorage.GetOwnerIndexPages(id).ShouldBeEmpty(); }
            IndexOwners(recovered).Keys.ShouldBe(before.Keys);
        }
        finally { await recovered.DisposeAsync(); }

        // Assert: preflight is stronger than eventual cleanup for a deterministic budget refusal.
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        abandoned.ShouldBeEmpty();
    }

    internal static Dictionary<ulong, int> IndexOwners(GraphDatabase database)
    {
        var result = new Dictionary<ulong, int>();
        for (long page = 1; page < database.DataStorage.PageManager.PageCount; page++)
        {
            if (!database.DataStorage.FreeSpaceMap.IsAllocated((PageId)page)) { continue; }
            using var handle = database.DataStorage.PageManager.GetPage((PageId)page);
            if (handle.Page.Type == PageType.Index)
            { result[handle.Page.OwnerId] = result.GetValueOrDefault(handle.Page.OwnerId) + 1; }
        }
        return result;
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - A build interrupted after allocation reclaims orphan owners on open (#1644)")]
    [InlineData("cancel", false)] [InlineData("cancel", true)]
    [InlineData("page", false)] [InlineData("page", true)]
    [InlineData("disk", false)] [InlineData("disk", true)]
    public async Task CreateIndexAsync_InterruptedAllocation_ShouldCollectOrphanOnOpenAsync(string fault, bool text)
    {
        // Arrange: the hook runs after an actual entry insertion inside the physical bracket.
        var strategy = new FaultInjectingJournalStorageStrategy(durable: true);
        var options = GraphTextIndexTests.Quiet(); options.StorageStrategy = strategy;
        await using var engine = GraphDatabaseEngine.Create("build-fault", options);
        var database = await engine.CreateDatabaseAsync("source", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await GraphTextIndexReviewTests.PopulateAsync(session, 10, "abcdefghij");
        var before = IndexOwners(database).Keys.ToArray();
        ulong abandoned = 0;
        int injected = 0;
        using var cancellation = new CancellationTokenSource();
        database.Store.AfterTextIndexMutation = stage =>
        {
            if (stage != "build-entry" || injected != 0) { return; }
            injected++;
            abandoned = IndexOwners(database).Keys.Except(before).Single();
            if (fault == "cancel") { cancellation.Cancel(); return; }
            if (fault == "page")
            {
                var faults = strategy.Faults("source");
                faults.FailNextPageWrites(1);
                try { database.DataStorage.PageManager.FlushPage(database.DataStorage.GetOwnerIndexPages(abandoned).First()); }
                finally { faults.PageWriteFailures.ShouldBe(1); }
                return;
            }
            using var refusal = FaultInjectingJournalStorageStrategy.FailJournalWrites(1);
            try { database.DataStorage.WriteBackDirtyPages(int.MaxValue); }
            finally { refusal.Remaining.ShouldBe(0); }
        };

        // Act
        Exception? failure = null;
        try { await session.ExecuteAsync($"CREATE {(text ? "TEXT " : "")}INDEX t FOR (n:N) ON (n.p)", cancellationToken: cancellation.Token); }
        catch (Exception error) { failure = error; }
        finally { database.Store.AfterTextIndexMutation = null; }
        injected.ShouldBe(1);
        failure.ShouldNotBeNull();
        abandoned.ShouldBeGreaterThan(2UL);
        int pages = database.DataStorage.GetOwnerIndexPages(abandoned).Count;
        _output.WriteLine($"{fault}/{text}: owner={abandoned}; pages after refusal={pages}; error={failure.GetType().Name}");
        if (fault != "disk")
        {
            database.DataStorage.WriteBackDirtyPages(int.MaxValue);
            database.DataStorage.WriteAheadJournal.Flush(forceDurable: true);
        }
        strategy.CopyDatabase("source", "image");

        // Assert: both opens converge, with no registration-free owner left behind.
        for (int reopen = 0; reopen < 2; reopen++)
        {
            var recovered = await engine.OpenDatabaseAsync("image", CancellationToken.None);
            try
            {
                if (reopen == 0 && fault != "disk")
                {
                    recovered.DataStorage.GetOwnerIndexPages(abandoned).Count.ShouldBe(pages);
                    GraphTextIndexTests.Registrations(recovered).ShouldContain(entry => entry.Id == abandoned && entry.Root < 0);
                }
                for (int pass = 0; pass < pages + 4; pass++) { recovered.Coordinator.RunVersionPurgePass(CancellationToken.None); }
                recovered.DataStorage.GetOwnerIndexPages(abandoned).ShouldBeEmpty();
                GraphTextIndexTests.Registrations(recovered).ShouldNotContain(entry => entry.Id == abandoned);
                await using var reader = await recovered.CreateSessionAsync(CancellationToken.None);
                (await GraphTextIndexTests.RowsAsync(reader, "MATCH (n:N) RETURN n.k")).Count.ShouldBe(10);
                (await GraphSchema.Open(recovered, reader).GetIndexesAsync(CancellationToken.None)).ShouldBeEmpty();
            }
            finally { await recovered.DisposeAsync(); }
        }
    }
}
