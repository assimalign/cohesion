using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage.Tests;

public sealed partial class GraphStoreTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Labels: missing tree registration refuses open instead of hiding nodes (#1534)")]
    public async Task Open_MissingLabelRegistration_ShouldRefuseAsync()
    {
        // Arrange
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), ["N"], _empty),
            TestLabelIds.All, writer, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(writer, CancellationToken.None);
        using var registrations = fixture.Storage.GetUnitIterator(1);
        registrations.MoveNext().ShouldBeTrue();
        var registration = registrations.Current;
        byte[] bytes = registration.Data.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(18), 123);

        // Act: replace only the registration's object ID, leaving a readable but unregistered tree.
        using (var bracket = fixture.Storage.BeginTransaction())
        {
            fixture.Storage.UpdateEntry(bracket, registration.PageId, registration.SlotIndex, bytes);
            bracket.Commit();
        }

        // Assert
        Should.Throw<StorageCorruptionException>(() => GraphStore.Open(fixture.Storage, fixture.Coordinator));
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Labels: interrupted postings roll back the statement and permit another write (#1534)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateNodeAsync_InterruptedPostingChanges_ShouldRestoreRecordAndTreeAsync(bool cancel)
    {
        // Arrange
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), ["N"], Properties("x", 0)),
            TestLabelIds.All, writer, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        int changed = 0;
        fixture.Store.OnLabelIndexChanged = () =>
        {
            changed++;
            if (cancel) { cancellation.Cancel(); }
            else { throw new InvalidOperationException(); }
        };

        // Act
        if (cancel)
        { await Should.ThrowAsync<OperationCanceledException>(() => fixture.Store.UpdateNodeAsync(node.Id, ["M"], Properties("x", 1), TestLabelIds.All, writer, cancellation.Token).AsTask()); }
        else
        { await Should.ThrowAsync<InvalidOperationException>(() => fixture.Store.UpdateNodeAsync(node.Id, ["M"], Properties("x", 1), TestLabelIds.All, writer, CancellationToken.None).AsTask()); }
        fixture.Store.OnLabelIndexChanged = null;

        // Assert
        changed.ShouldBe(1);
        fixture.Store.GetNodes("N", writer.Snapshot).ShouldHaveSingleItem().Properties["x"].ShouldBe(0);
        fixture.Store.GetNodes("M", writer.Snapshot).ShouldBeEmpty();
        await fixture.Store.UpdateNodeAsync(node.Id, ["M"], Properties("x", 2), TestLabelIds.All, writer, CancellationToken.None);
        fixture.Store.GetNodes("M", writer.Snapshot).ShouldHaveSingleItem().Properties["x"].ShouldBe(2);
        await fixture.Coordinator.RollbackAsync(writer, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Labels: decodes and range visits depend only on matching postings (#1534)")]
    public async Task GetNodes_SparseLabels_ShouldVisitOnlyMatchingPostingsAsync()
    {
        // Arrange: the same Person fraction as R4, at one tenth of its diagnostic distribution.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var fixture = new Fixture();
        var write = await fixture.Begin();
        for (int i = 0; i < 4_359; i++)
        {
            await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(),
                [i < 30 ? "Person" : "N"], _empty), TestLabelIds.All, write, timeout.Token);
        }
        await fixture.Coordinator.CommitAsync(write, timeout.Token);
        var read = await fixture.Begin();
        long baseline = long.MaxValue, indexed = long.MaxValue, visited = long.MaxValue;

        // Act: compare the old algorithm with the index, taking the minimum of three rounds.
        for (int round = 0; round < 3; round++)
        {
            long before = fixture.Store.NodeRecordsDecoded;
            fixture.Store.GetNodes(null, read.Snapshot).Count(node => node.Labels.Contains("Person")).ShouldBe(30);
            baseline = Math.Min(baseline, fixture.Store.NodeRecordsDecoded - before);
            before = fixture.Store.NodeRecordsDecoded;
            long entries = fixture.Store.LabelEntriesVisited;
            var found = fixture.Store.GetNodes("Person", read.Snapshot);
            found.Count.ShouldBe(30);
            found.Select(node => node.Id).ShouldBe(found.Select(node => node.Id).Order());
            indexed = Math.Min(indexed, fixture.Store.NodeRecordsDecoded - before);
            visited = Math.Min(visited, fixture.Store.LabelEntriesVisited - entries);
        }

        // Assert
        baseline.ShouldBe(4_359);
        indexed.ShouldBe(30);
        visited.ShouldBe(30);
        (indexed * 100).ShouldBeLessThan(baseline * 10);
        _output.WriteLine($"Nodes=4359, Persons=30: before decodes={baseline}; after decodes={indexed}; postings={visited}; minimum of 3 rounds.");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Labels: replacements, rollback, purge, recreate and reopen preserve snapshots (#1534)")]
    public async Task GetNodes_LabelVersions_ShouldSurviveRollbackPurgeAndReopenAsync()
    {
        // Arrange: label bytes exceed the B+Tree key limit; keys still use catalog identities.
        await using var fixture = new Fixture();
        string longLabel = new('x', 1_100);
        var ids = new Dictionary<string, Guid>(TestLabelIds.All) { [longLabel] = Guid.NewGuid() };
        var seed = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(),
            ["N", longLabel], Properties("x", 0)), ids, seed, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(seed, CancellationToken.None);
        var old = await fixture.Begin();
        var write = await fixture.Begin();

        // Act: unchanged labels must move too, because a property-only update relocates the record.
        await fixture.Store.UpdateNodeAsync(node.Id, ["N", longLabel], Properties("x", 1), ids, write, CancellationToken.None);
        fixture.Store.GetNodes("N", write.Snapshot).Single().Properties["x"].ShouldBe(1);
        await fixture.Store.UpdateNodeAsync(node.Id, ["M", longLabel], Properties("x", 2), ids, write, CancellationToken.None);
        fixture.Store.GetNodes("N", write.Snapshot).ShouldBeEmpty();
        fixture.Store.GetNodes("M", write.Snapshot).ShouldHaveSingleItem();
        await fixture.Coordinator.CommitAsync(write, CancellationToken.None);
        fixture.Store.GetNodes("N", old.Snapshot).Single().Properties["x"].ShouldBe(0);
        fixture.Store.GetNodes("M", old.Snapshot).ShouldBeEmpty();
        var rollback = await fixture.Begin();
        await fixture.Store.DeleteNodeAsync(node.Id, false, rollback, CancellationToken.None);
        fixture.Store.GetNodes(longLabel, rollback.Snapshot).ShouldBeEmpty();
        await fixture.Coordinator.RollbackAsync(rollback, CancellationToken.None);
        var current = await fixture.Begin();
        fixture.Store.GetNodes(longLabel, current.Snapshot).Single().Properties["x"].ShouldBe(2);
        await fixture.Coordinator.RollbackAsync(current, CancellationToken.None);
        await fixture.Coordinator.RollbackAsync(old, CancellationToken.None);
        fixture.Coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBeGreaterThan(0);
        var remove = await fixture.Begin();
        await fixture.Store.DeleteNodeAsync(node.Id, false, remove, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(remove, CancellationToken.None);
        fixture.Coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBeGreaterThan(0);
        // A dropped/recreated catalog label can reuse the name with a new identity.
        ids["N"] = Guid.NewGuid();
        var recreate = await fixture.Begin();
        var replacement = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(),
            ["N"], _empty), ids, recreate, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(recreate, CancellationToken.None);
        var reopened = GraphStore.Open(fixture.Storage, fixture.Coordinator);
        var reader = await fixture.Begin();

        // Assert
        reopened.GetNodes("N", reader.Snapshot).ShouldHaveSingleItem().Id.ShouldBe(replacement.Id);
        reopened.GetNodes("M", reader.Snapshot).ShouldBeEmpty();
        reopened.GetNodes(longLabel, reader.Snapshot).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Labels: empty, canceled and exceptional scans release cursors (#1534)")]
    public async Task ScanLabels_EarlyExits_ShouldLeaveStoreUsableAsync()
    {
        // Arrange
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), ["N"], _empty),
            TestLabelIds.All, writer, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        fixture.Store.OnLabelEntryRead = cancellation.Cancel;

        // Act / Assert: cancellation after a cursor opened, pre-cancel on empty and nonempty ranges,
        // an injected consumer failure, normal empty completion and normal exhaustion.
        Should.Throw<OperationCanceledException>(() => fixture.Store.ScanLabels(["N"], writer.Snapshot, cancellation.Token));
        fixture.Store.OnLabelEntryRead = null;
        Should.Throw<OperationCanceledException>(() => fixture.Store.ScanLabels(["Missing"], writer.Snapshot, cancellation.Token));
        Should.Throw<OperationCanceledException>(() => fixture.Store.ScanLabels(["N"], writer.Snapshot, cancellation.Token));
        fixture.Store.OnLabelEntryRead = () => throw new InvalidOperationException();
        Should.Throw<InvalidOperationException>(() => fixture.Store.ScanLabels(["N"], writer.Snapshot, CancellationToken.None));
        fixture.Store.OnLabelEntryRead = null;
        fixture.Store.ScanLabels(["Missing"], writer.Snapshot, CancellationToken.None).ShouldBeEmpty();
        fixture.Store.GetNodes("N", writer.Snapshot).ShouldHaveSingleItem();
        await fixture.Store.DeleteNodeAsync(node.Id, false, writer, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(writer, CancellationToken.None);
    }
}
