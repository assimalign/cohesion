using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Testing;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphExactIndexRetirementTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - A pinned dropped exact tree validates and finds a Snapshot writer's own values (#1644)")]
    [InlineData(false)] [InlineData(true)]
    public async Task Execute_PinnedDroppedExactIndex_ShouldMaintainOwnWritesAsync(bool oversized)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("exact-pinned", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var current = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await current.ExecuteAsync("CREATE INDEX exact_p FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        await using var transaction = await writer.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await current.ExecuteAsync("DROP INDEX exact_p", cancellationToken: CancellationToken.None);

        // Act / Assert
        if (oversized)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync(
                "INSERT (:N {p:$pattern})", GraphTextIndexTests.Bind(new string('x', 2048)), CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02009");
            (await GraphTextIndexTests.RowsAsync(current, "MATCH (n:N) RETURN n.p")).ShouldBeEmpty();
        }
        else
        {
            await writer.ExecuteAsync("INSERT (:N {p:'x'})", cancellationToken: CancellationToken.None);
            (await GraphTextIndexTests.RowsAsync(writer, "EXPLAIN MATCH (n:N {p:'x'}) RETURN n.p")).ShouldContain(row => Equals(row[2], "IndexSeek"));
            (await GraphTextIndexTests.RowsAsync(writer, "MATCH (n:N {p:'x'}) RETURN n.p")).ShouldHaveSingleItem()[0].ShouldBe("x");
            await transaction.CommitAsync(CancellationToken.None);
            (await GraphTextIndexTests.RowsAsync(current, "MATCH (n:N {p:'x'}) RETURN n.p")).ShouldHaveSingleItem();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Exact tree owner pages survive an old pin and retire after it releases (#1644)")]
    public async Task DropIndexAsync_ExactTree_ShouldRespectSnapshotAndFreeOwnerPagesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("exact-retire", GraphTextIndexTests.Quiet());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await using var old = await database.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("INSERT (:N {p:'abc'})", cancellationToken: CancellationToken.None);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await writer.ExecuteAsync("FOR k IN [" + string.Join(',', Enumerable.Range(1, 512)) + "] INSERT (:N {p:$pattern})",
            GraphTextIndexTests.Bind(new string('x', 300)), guard.Token);
        using var events = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Informational);
        await writer.ExecuteAsync("CREATE INDEX exact_p FOR (n:N) ON (n.p)", cancellationToken: guard.Token);
        await using var snapshot = await old.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        ulong id = database.Store.GetIndexIdentities(snapshot.Context.Snapshot).Single().Id;
        int pages = database.DataStorage.GetOwnerIndexPages(id).Count;
        pages.ShouldBeGreaterThan(64);
        long reclaimed = database.Store.IndexOwnerPagesReclaimed;

        // Act / Assert
        await writer.ExecuteAsync("DROP INDEX exact_p", cancellationToken: CancellationToken.None);
        int reads = 0, passes = 0, overlaps = 0, reading = 0;
        using var started = new ManualResetEventSlim();
        var race = Stopwatch.StartNew();
        var readTask = Task.Run(async () =>
        {
            Volatile.Write(ref reading, 1); started.Set();
            do
            {
                (await GraphTextIndexTests.RowsAsync(old, "MATCH (n:N {p:'abc'}) RETURN n.p")).ShouldHaveSingleItem();
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
        database.Coordinator.RunVersionPurgePass(CancellationToken.None);
        database.DataStorage.GetOwnerIndexPages(id).Count.ShouldBe(pages);
        (await GraphTextIndexTests.RowsAsync(old, "MATCH (n:N {p:'abc'}) RETURN n.p")).ShouldHaveSingleItem();
        await snapshot.RollbackAsync(CancellationToken.None);
        for (int pass = 0; pass < (pages + 63) / 64 + 2 && (database.DataStorage.GetOwnerIndexPageCount(id) != 0); pass++)
        { database.Coordinator.RunVersionPurgePass(guard.Token); }
        database.DataStorage.GetOwnerIndexPages(id).ShouldBeEmpty();
        (database.Store.IndexOwnerPagesReclaimed - reclaimed).ShouldBe(pages);
        events.Events.Count(item => item.EventId == 16).ShouldBe(1);
        events.Events.Single(item => item.EventId == 16).EventName.ShouldBe("IndexBuildBegan");
        events.Events.Single(item => item.EventId == 16).Opcode.ShouldBe(EventOpcode.Info);
        events.Events.Count(item => item.EventId == 17).ShouldBe(1);
        events.Events.Single(item => item.EventId == 17).EventName.ShouldBe("IndexBuildEnded");
        events.Events.Single(item => item.EventId == 17).Opcode.ShouldBe(EventOpcode.Info);
        events.Events.Where(item => item.EventId == 18).Sum(item => (int)item.Payload![2]!).ShouldBe(pages);
        GraphTextIndexTests.Registrations(database).ShouldNotContain(entry => entry.Id == id);
        (await GraphTextIndexTests.RowsAsync(writer, "MATCH (n:N {p:'abc'}) RETURN n.p")).ShouldHaveSingleItem();
    }
}
