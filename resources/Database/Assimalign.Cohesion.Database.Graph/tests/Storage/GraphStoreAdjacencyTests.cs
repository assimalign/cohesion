using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

public sealed partial class GraphStoreTests
{
    private readonly ITestOutputHelper _output;
    public GraphStoreTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Adjacency: unrelated types and directions add zero relationship decodes (#1532)")]
    public async Task GetIncidentAsync_UnrelatedEdges_ShouldDecodeOnlySelectedRangesAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var fixture = new Fixture();
        var write = await fixture.Begin();
        var a = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), [], _empty), write, timeout.Token);
        var b = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), [], _empty), write, timeout.Token);
        var wanted = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var other = Guid.Parse("10000000-0000-0000-0000-000000000002");
        for (int i = 0; i < 8; i++) { await CreateEdgeAsync(a.Id, b.Id, wanted, "T"); }
        await CreateEdgeAsync(a.Id, a.Id, wanted, "T");

        // Act / Assert: minimum of three deterministic rounds at each size, including leaf splits.
        foreach (int noise in new[] { 0, 2048 })
        {
            for (int i = 0; i < noise; i++)
            {
                await CreateEdgeAsync(a.Id, b.Id, other, "Other");
                await CreateEdgeAsync(b.Id, a.Id, wanted, "T");
            }
            long before = long.MaxValue, after = long.MaxValue;
            for (int round = 0; round < 3; round++)
            {
                long start = fixture.Store.IncidentRecordsDecoded;
                (await fixture.Store.GetIncidentAsync(a.Id, write.Snapshot, timeout.Token)).Count.ShouldBe(9 + 2 * noise);
                before = Math.Min(before, fixture.Store.IncidentRecordsDecoded - start);
                start = fixture.Store.IncidentRecordsDecoded;
                var found = await SelectedAsync(fixture.Store, a.Id, GraphIncidentDirection.Outgoing, [wanted, wanted], write.Snapshot, timeout.Token);
                found.Count.ShouldBe(9);
                after = Math.Min(after, fixture.Store.IncidentRecordsDecoded - start);
            }
            before.ShouldBe(9 + 2 * noise);
            after.ShouldBe(9);
            _output.WriteLine($"Unrelated edges={2 * noise}: unfiltered decodes={before}; filtered decodes={after}; minimum of 3 rounds.");
        }
        await fixture.Coordinator.RollbackAsync(write, timeout.Token);

        async ValueTask CreateEdgeAsync(ulong from, ulong to, Guid typeId, string type)
            => await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), from, to, type, _empty), typeId, write, timeout.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Adjacency: direction and catalog identity preserve versions, rollback, delete and restart (#1532)")]
    [InlineData(GraphIncidentDirection.Outgoing)]
    [InlineData(GraphIncidentDirection.Incoming)]
    [InlineData(GraphIncidentDirection.Both)]
    public async Task GetIncidentAsync_MvccChanges_ShouldPreserveFilteredRangesAsync(GraphIncidentDirection direction)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = new Fixture();
        var seed = await fixture.Begin();
        var type = Guid.NewGuid();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), [], _empty), seed, timeout.Token);
        var edge = await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), node.Id, node.Id, "T", Properties("x", 1)), type, seed, timeout.Token);
        await fixture.Coordinator.CommitAsync(seed, timeout.Token);
        var old = await fixture.Begin();
        var update = await fixture.Begin();

        // Act
        await fixture.Store.UpdateRelationshipAsync(edge.Id, Properties("x", 2), update, timeout.Token);
        (await SelectedAsync(fixture.Store, node.Id, direction, [type], update.Snapshot, timeout.Token)).Single().Properties["x"].ShouldBe(2);
        await fixture.Coordinator.CommitAsync(update, timeout.Token);
        (await SelectedAsync(fixture.Store, node.Id, direction, [type], old.Snapshot, timeout.Token)).Single().Properties["x"].ShouldBe(1);
        var deleting = await fixture.Begin();
        await fixture.Store.DeleteRelationshipAsync(edge.Id, deleting, timeout.Token);
        (await SelectedAsync(fixture.Store, node.Id, direction, [type], deleting.Snapshot, timeout.Token)).ShouldBeEmpty();
        await fixture.Coordinator.RollbackAsync(deleting, timeout.Token);
        var read = await fixture.Begin();

        // Assert
        (await SelectedAsync(fixture.Store, node.Id, direction, [type], read.Snapshot, timeout.Token)).Single().Properties["x"].ShouldBe(2);
        (await SelectedAsync(fixture.Store, node.Id, direction, [Guid.NewGuid()], read.Snapshot, timeout.Token)).ShouldBeEmpty();
        (await SelectedAsync(fixture.Store, node.Id, direction, [], read.Snapshot, timeout.Token)).ShouldBeEmpty();
        await fixture.Coordinator.RollbackAsync(read, timeout.Token);
        await fixture.Coordinator.RollbackAsync(old, timeout.Token);
        fixture.Coordinator.RunVersionPurgePass(timeout.Token).ShouldBeGreaterThan(0);
        // Open reconstructs the directories and attaches the same version-3 registrations.
        var reopened = GraphStore.Open(fixture.Storage, fixture.Coordinator);
        var current = await fixture.Begin();
        (await SelectedAsync(reopened, node.Id, direction, [type], current.Snapshot, timeout.Token)).Single().Properties["x"].ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Adjacency: pre-cancellation is observed even for empty ranges (#1532)")]
    public async Task GetIncidentAsync_CanceledEmptyLookup_ShouldThrowAsync()
    {
        // Arrange
        await using var fixture = new Fixture();
        var read = await fixture.Begin();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(() => SelectedAsync(fixture.Store, 1, GraphIncidentDirection.Both, [], read.Snapshot, cancellation.Token));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => SelectedAsync(fixture.Store, 1, (GraphIncidentDirection)42, null, read.Snapshot, CancellationToken.None));
        await Should.ThrowAsync<ArgumentNullException>(() => SelectedAsync(fixture.Store, 1, GraphIncidentDirection.Both, null, null!, CancellationToken.None));
    }

    private static async Task<List<StoredGraphRelationship>> SelectedAsync(GraphStore store, ulong node, GraphIncidentDirection direction,
        IReadOnlyCollection<Guid>? types, TransactionSnapshot snapshot, CancellationToken token)
    {
        var result = new List<StoredGraphRelationship>();
        await foreach (var edge in store.GetIncidentAsync(node, direction, types, snapshot, token)) { result.Add(edge); }
        return result;
    }
}
