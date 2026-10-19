using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

[CollectionDefinition(nameof(GraphAdjacencyConcurrencyTests), DisableParallelization = true)]
[Collection(nameof(GraphAdjacencyConcurrencyTests))]
public sealed class GraphAdjacencyConcurrencyTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Adjacency: overlapping updates preserve pinned ranges and every cursor exit releases its resources (#1532)")]
    [InlineData("Complete")]
    [InlineData("Break")]
    [InlineData("Throw")]
    [InlineData("Cancel")]
    public async Task GetIncidentAsync_ConcurrentReplacementAndExit_ShouldRetainSnapshotAndReleaseCursorAsync(string exit)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var storage = GraphStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "adjacency-race");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var store = GraphStore.Open(storage, coordinator);
        var type = Guid.NewGuid();
        var seed = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        var node = await store.CreateNodeAsync(new StoredGraphNode((ulong)storage.ReserveTransactionSequence(), [], new Dictionary<string, object?>()), seed, timeout.Token);
        var ids = new List<ulong>();
        for (int i = 0; i < 3; i++)
        {
            ulong id = (ulong)storage.ReserveTransactionSequence();
            await store.CreateRelationshipAsync(new StoredGraphRelationship(id, node.Id, node.Id, "T", Properties(0)), type, seed, timeout.Token);
            ids.Add(id);
        }
        await coordinator.CommitAsync(seed, timeout.Token);
        var old = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        var elapsed = Stopwatch.StartNew();
        int overlaps = 0, exits = 0;
        long before = store.IncidentRecordsDecoded;

        // Act: suspend a reader between records while a real concurrent writer replaces all entries.
        do
        {
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            await using (var cursor = store.GetIncidentAsync(node.Id, GraphIncidentDirection.Both, [type], old.Snapshot, canceled.Token).GetAsyncEnumerator())
            {
                (await cursor.MoveNextAsync()).ShouldBeTrue();
                cursor.Current.Properties["x"].ShouldBe(0);
                int value = overlaps + 1;
                await Task.Run(async () =>
                {
                    var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
                    foreach (ulong id in ids) { await store.UpdateRelationshipAsync(id, Properties(value), writer, timeout.Token); }
                    if (value % 2 == 0) { await coordinator.RollbackAsync(writer, timeout.Token); }
                    else { await coordinator.CommitAsync(writer, timeout.Token); }
                }, timeout.Token);
                overlaps++;
                if (exit == "Complete")
                {
                    int count = 1;
                    while (await cursor.MoveNextAsync()) { cursor.Current.Properties["x"].ShouldBe(0); count++; }
                    count.ShouldBe(3);
                }
                else if (exit == "Cancel")
                {
                    canceled.Cancel();
                    await Should.ThrowAsync<OperationCanceledException>(async () => await cursor.MoveNextAsync());
                }
                else if (exit == "Throw")
                {
                    await Should.ThrowAsync<InvalidOperationException>(async () =>
                    {
                        await using var throwing = store.GetIncidentAsync(node.Id, GraphIncidentDirection.Outgoing, [type], old.Snapshot, timeout.Token).GetAsyncEnumerator();
                        (await throwing.MoveNextAsync()).ShouldBeTrue();
                        throw new InvalidOperationException();
                    });
                }
                exits++;
            }
            // A reader's early exit must not retain a latch and block a following mutation.
            var next = await coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
            await store.UpdateRelationshipAsync(ids[0], Properties(99), next, timeout.Token);
            await coordinator.RollbackAsync(next, timeout.Token);
        } while ((elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || overlaps < 3) && elapsed.Elapsed < TimeSpan.FromSeconds(20));

        // Assert
        overlaps.ShouldBeGreaterThanOrEqualTo(3);
        exits.ShouldBe(overlaps);
        (store.IncidentRecordsDecoded - before).ShouldBeGreaterThanOrEqualTo(overlaps * (exit == "Complete" ? 3 : 1));
        await coordinator.RollbackAsync(old, timeout.Token);
    }

    private static IReadOnlyDictionary<string, object?> Properties(int value) => new Dictionary<string, object?> { ["x"] = value };
}
