using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

[CollectionDefinition(nameof(GraphAdjacencyConcurrencyTests), DisableParallelization = true)]
[Collection(nameof(GraphAdjacencyConcurrencyTests))]
public sealed class GraphAdjacencyConcurrencyTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Adjacency: Both retains incoming snapshot after a commit between ranges (#1532)")]
    public async Task GetIncidentAsync_WriterCommitsBetweenDirections_ShouldRetainIncomingSnapshotAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = new GraphAdjacencyFixture();
        var seed = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        var hub = await fixture.NodeAsync(seed, timeout.Token);
        var peer = await fixture.NodeAsync(seed, timeout.Token);
        var outgoing = await fixture.EdgeAsync(hub.Id, peer.Id, 0, seed, timeout.Token);
        var changed = await fixture.EdgeAsync(peer.Id, hub.Id, 0, seed, timeout.Token);
        var deleted = await fixture.EdgeAsync(peer.Id, hub.Id, 0, seed, timeout.Token);
        await fixture.Coordinator.CommitAsync(seed, timeout.Token);
        var reader = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        await using var cursor = fixture.Store.GetIncidentAsync(hub.Id, GraphIncidentDirection.Both, [fixture.Type], reader.Snapshot, timeout.Token).GetAsyncEnumerator();

        // Act: the only outgoing entry is consumed; the incoming range has not opened yet.
        (await cursor.MoveNextAsync()).ShouldBeTrue();
        cursor.Current.Id.ShouldBe(outgoing.Id);
        ulong added = await DedicatedTestTask.Run(async () =>
        {
            var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
            await fixture.Store.UpdateRelationshipAsync(changed.Id, Properties(1), writer, timeout.Token);
            await fixture.Store.DeleteRelationshipAsync(deleted.Id, writer, timeout.Token);
            var edge = await fixture.EdgeAsync(peer.Id, hub.Id, 1, writer, timeout.Token);
            await fixture.Coordinator.CommitAsync(writer, timeout.Token);
            return edge.Id;
        }, timeout.Token);
        var incoming = new List<StoredGraphRelationship>();
        while (await cursor.MoveNextAsync()) { incoming.Add(cursor.Current); }

        // Assert: opening the incoming range still uses the original reader's snapshot.
        incoming.Select(edge => edge.Id).Order().ShouldBe(new[] { changed.Id, deleted.Id }.Order());
        incoming.ShouldAllBe(edge => edge.SourceId == peer.Id && edge.TargetId == hub.Id && Equals(edge.Properties["x"], 0));
        await fixture.Coordinator.RollbackAsync(reader, timeout.Token);
        var current = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        var latest = await fixture.ReadAsync(hub.Id, current.Snapshot, timeout.Token);
        latest.Select(edge => edge.Id).Order().ShouldBe(new[] { outgoing.Id, changed.Id, added }.Order());
        latest.Where(edge => edge.TargetId == hub.Id).ShouldAllBe(edge => Equals(edge.Properties["x"], 1));
        await fixture.Coordinator.RollbackAsync(current, timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Adjacency: independent readers and writers overlap and return complete snapshots (#1532)")]
    public async Task GetIncidentAsync_ConcurrentReaderAndWriterLoops_ShouldObserveOnlyValidSnapshotsAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var start = new Barrier(2);
        await using var fixture = new GraphAdjacencyFixture();
        var seed = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        var hub = await fixture.NodeAsync(seed, timeout.Token);
        var peer = await fixture.NodeAsync(seed, timeout.Token);
        var outgoing = await fixture.EdgeAsync(hub.Id, peer.Id, 0, seed, timeout.Token);
        var incoming = await fixture.EdgeAsync(peer.Id, hub.Id, 0, seed, timeout.Token);
        var replaced = await fixture.EdgeAsync(peer.Id, hub.Id, 0, seed, timeout.Token);
        await fixture.Coordinator.CommitAsync(seed, timeout.Token);
        var states = new ConcurrentDictionary<int, (TransactionSequence Sequence, ulong ReplacedId)>();
        states[0] = (seed.Sequence, replaced.Id);
        const int rounds = 64;
        int reading = 0, writing = 0, reads = 0, writes = 0, readOverlaps = 0, writeOverlaps = 0;

        // Act: independent loops yield inside operations so seeks and record reads can race
        // mutation brackets. Two barrier phases per round keep both operation lifetimes open.
        var readerTask = DedicatedTestTask.Run(async () =>
        {
            for (int iteration = 0; iteration < rounds; iteration++)
            {
                Volatile.Write(ref reading, 1);
                try
                {
                    start.SignalAndWait(timeout.Token);
                    if (Volatile.Read(ref writing) != 0) { Interlocked.Increment(ref readOverlaps); }
                    var reader = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
                    try
                    {
                        var expected = states.Where(state => reader.Snapshot.IsVisible(state.Value.Sequence)).MaxBy(state => state.Key);
                        var observed = new List<StoredGraphRelationship>();
                        await foreach (var edge in fixture.Store.GetIncidentAsync(hub.Id, GraphIncidentDirection.Both, [fixture.Type], reader.Snapshot, timeout.Token))
                        {
                            observed.Add(edge);
                            await Task.Yield();
                        }
                        observed.Select(edge => edge.Id).Order().ShouldBe(new[] { outgoing.Id, incoming.Id, expected.Value.ReplacedId }.Order());
                        observed.ShouldAllBe(edge => Equals(edge.Properties["x"], expected.Key));
                        observed.ShouldAllBe(edge => edge.SourceId != edge.TargetId &&
                            (edge.Id == outgoing.Id ? edge.SourceId == hub.Id && edge.TargetId == peer.Id : edge.SourceId == peer.Id && edge.TargetId == hub.Id));
                        Interlocked.Increment(ref reads);
                    }
                    finally { await fixture.Coordinator.RollbackAsync(reader, timeout.Token); }
                }
                finally { start.SignalAndWait(timeout.Token); Volatile.Write(ref reading, 0); }
                await Task.Yield();
            }
        }, timeout.Token);
        var writerTask = DedicatedTestTask.Run(async () =>
        {
            ulong prior = replaced.Id;
            for (int version = 1; version <= rounds; version++)
            {
                Volatile.Write(ref writing, 1);
                try
                {
                    start.SignalAndWait(timeout.Token);
                    var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
                    await fixture.Store.UpdateRelationshipAsync(outgoing.Id, Properties(version), writer, timeout.Token);
                    if (Volatile.Read(ref reading) != 0) { Interlocked.Increment(ref writeOverlaps); }
                    await Task.Yield();
                    await fixture.Store.UpdateRelationshipAsync(incoming.Id, Properties(version), writer, timeout.Token);
                    await fixture.Store.DeleteRelationshipAsync(prior, writer, timeout.Token);
                    var next = await fixture.EdgeAsync(peer.Id, hub.Id, version, writer, timeout.Token);
                    states[version] = (writer.Sequence, next.Id);
                    await Task.Yield();
                    await fixture.Coordinator.CommitAsync(writer, timeout.Token);
                    prior = next.Id;
                    Interlocked.Increment(ref writes);
                }
                finally { start.SignalAndWait(timeout.Token); Volatile.Write(ref writing, 0); }
                await Task.Yield();
            }
        }, timeout.Token);
        await Task.WhenAll(readerTask, writerTask);

        // Assert: both loops completed their bounded work and saw the other inside an operation.
        reads.ShouldBe(rounds);
        writes.ShouldBe(rounds);
        readOverlaps.ShouldBe(reads);
        writeOverlaps.ShouldBe(writes);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Adjacency: cancellation between directions prevents reading incoming records (#1532)")]
    public async Task GetIncidentAsync_CanceledBetweenDirections_ShouldStopBeforeIncomingAsync()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var fixture = new GraphAdjacencyFixture();
        var writer = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var hub = await fixture.NodeAsync(writer, CancellationToken.None);
        var peer = await fixture.NodeAsync(writer, CancellationToken.None);
        await fixture.EdgeAsync(hub.Id, peer.Id, 0, writer, CancellationToken.None);
        await fixture.EdgeAsync(peer.Id, hub.Id, 0, writer, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(writer, CancellationToken.None);
        var reader = await fixture.Coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await using var cursor = fixture.Store.GetIncidentAsync(hub.Id, GraphIncidentDirection.Both, [fixture.Type], reader.Snapshot, cancellation.Token).GetAsyncEnumerator();
        (await cursor.MoveNextAsync()).ShouldBeTrue();
        long decoded = fixture.Store.IncidentRecordsDecoded;

        // Act / Assert
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await cursor.MoveNextAsync());
        fixture.Store.IncidentRecordsDecoded.ShouldBe(decoded);
        await fixture.Coordinator.RollbackAsync(reader, CancellationToken.None);
    }

    private static IReadOnlyDictionary<string, object?> Properties(int value) => new Dictionary<string, object?> { ["x"] = value };
}
