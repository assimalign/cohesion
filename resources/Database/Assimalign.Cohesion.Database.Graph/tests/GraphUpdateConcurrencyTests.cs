using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphUpdateConcurrencyTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - SET: ReadCommitted retakes its snapshot after waiting for the writer (#1149)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_QueuedReadCommittedIncrement_ShouldSerialize(bool explicitTransaction)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph", token);
        await using var first = await db.CreateSessionAsync(token);
        await using var second = await db.CreateSessionAsync(token);
        await first.ExecuteAsync("INSERT (:P {n:0})", cancellationToken: token);
        int queued = 0;
        var elapsed = Stopwatch.StartNew();

        // Act: run for a minimum duration and until the queue has actually been exercised.
        do
        {
            await first.ExecuteAsync("MATCH (a:P) SET a.n=0", cancellationToken: token);
            await using var blocker = await first.BeginTransactionAsync(IsolationLevel.Snapshot, token);
            await using var next = explicitTransaction ? await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, token) : null;
            await first.ExecuteAsync("MATCH (a:P) SET a.n=a.n+1", cancellationToken: token);
            var pending = second.ExecuteAsync("MATCH (a:P) SET a.n=a.n+1", cancellationToken: token).AsTask();
            await Task.WhenAny(pending, Task.Delay(10, token));
            pending.IsCompleted.ShouldBeFalse();
            await blocker.CommitAsync(token);
            (await pending).AffectedCount.ShouldBe(1);
            if (next is not null) { await next.CommitAsync(token); }
            await using var result = (DatabaseResultSet)await first.ExecuteAsync("MATCH (a:P) RETURN a.n", cancellationToken: token);
            await foreach (var row in result.GetRowsAsync(token)) { row.GetInt64(0).ShouldBe(2); }
            queued++;
        } while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || queued < 2) && elapsed.Elapsed < TimeSpan.FromSeconds(10));

        // Assert
        queued.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - SET: changed then restored values still conflict with stale Snapshot writers (#1149)")]
    public async Task Execute_RestoredValue_ShouldAbortStaleSnapshotWriter()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph");
        await using var first = await db.CreateSessionAsync();
        await using var stale = await db.CreateSessionAsync();
        await first.ExecuteAsync("INSERT (:P {x:1})");
        await using var snapshot = await stale.BeginTransactionAsync(IsolationLevel.Snapshot);

        // Act
        (await first.ExecuteAsync("MATCH (a:P) SET a.x=2 SET a.x=1")).AffectedCount.ShouldBe(1);

        // Assert
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await stale.ExecuteAsync("MATCH (a:P) SET a.x=3"));
        await snapshot.RollbackAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - SET: 30,000 collected nodes refresh with bounded allocation (#1149)")]
    public async Task Execute_SharedCollectAcrossThirtyThousandRows_ShouldAllocateLinearly()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph", token);
        await using var session = await db.CreateSessionAsync(token);
        await session.ExecuteAsync("FOR i IN $ids INSERT (:P {k:i})", new Dictionary<string, object?> { ["ids"] = Enumerable.Range(0, 30_000).ToArray() }, token);
        // Budget the physical writes and the extra read/group pipeline separately. Refresh
        // may add at most 64 MiB (about 2 KiB per row) beyond those measured costs.
        long before = GC.GetTotalAllocatedBytes(true);
        await session.ExecuteAsync("MATCH (b:P) SET b.seen = FALSE", cancellationToken: token);
        long baseline = GC.GetTotalAllocatedBytes(true) - before;
        before = GC.GetTotalAllocatedBytes(true);
        await using (var read = (DatabaseResultSet)await session.ExecuteAsync(
            "MATCH (a:P) WITH collect(a) AS ps MATCH (b:P) RETURN count(b)", cancellationToken: token))
        {
            await foreach (var row in read.GetRowsAsync(token)) { row.GetInt64(0).ShouldBe(30_000); }
        }
        long reads = GC.GetTotalAllocatedBytes(true) - before;
        before = GC.GetTotalAllocatedBytes(true);

        // Act
        var result = await session.ExecuteAsync("MATCH (a:P) WITH collect(a) AS ps MATCH (b:P) SET b.seen = TRUE", cancellationToken: token);
        long allocated = GC.GetTotalAllocatedBytes(true) - before;

        // Assert
        result.AffectedCount.ShouldBe(30_000);
        allocated.ShouldBeLessThan(baseline + reads + 64L * 1024 * 1024);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - SET: Snapshot uses first-updater-wins and old snapshots retain values (#1149)")]
    public async Task Execute_StaleSnapshotUpdate_ShouldAbortLoser()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("updates", new());
        var db = await engine.CreateDatabaseAsync("graph");
        await using var first = await db.CreateSessionAsync();
        await using var stale = await db.CreateSessionAsync();
        await first.ExecuteAsync("INSERT (:P {n:0})");
        await using var snapshot = await stale.BeginTransactionAsync(IsolationLevel.Snapshot);
        await first.ExecuteAsync("MATCH (a:P) SET a.n=1");
        await using var old = (DatabaseResultSet)await stale.ExecuteAsync("MATCH (a:P) RETURN a.n");
        await foreach (var row in old.GetRowsAsync(CancellationToken.None)) { row.GetInt64(0).ShouldBe(0); }

        // Act / Assert
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await stale.ExecuteAsync("MATCH (a:P) SET a.n=2"));
        await snapshot.RollbackAsync();
    }
}
