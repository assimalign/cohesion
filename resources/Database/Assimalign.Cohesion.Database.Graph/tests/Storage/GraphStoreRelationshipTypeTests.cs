using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
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
    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Type postings: split ranges, historical versions and recreated tokens preserve identity order (#1633)")]
    public async Task ScanRelationshipTypes_VersionsAndRecreatedTokens_ShouldPreserveSnapshotsAsync()
    {
        // Arrange: long type names never form keys; enough entries split the posting tree.
        await using var fixture = new Fixture();
        string type = new('x', 1_100);
        Guid token = Guid.NewGuid();
        var seed = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), [], _empty), TestLabelIds.All, seed, CancellationToken.None);
        var edges = new List<StoredGraphRelationship>();
        for (int i = 0; i < 240; i++)
        {
            edges.Add(await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(),
                node.Id, node.Id, type, Properties("x", 0)), token, seed, CancellationToken.None));
        }
        await fixture.Coordinator.CommitAsync(seed, CancellationToken.None);
        var old = await fixture.Begin();
        var write = await fixture.Begin();

        // Act: property-only replacements move the posting, even twice in one transaction.
        await fixture.Store.UpdateRelationshipAsync(edges[0].Id, Properties("x", 1), write, CancellationToken.None);
        await fixture.Store.UpdateRelationshipAsync(edges[0].Id, Properties("x", 2), write, CancellationToken.None);
        fixture.Store.ScanRelationshipTypes([type], write.Snapshot, CancellationToken.None)[0].Properties["x"].ShouldBe(2);
        await fixture.Store.DeleteRelationshipAsync(edges[1].Id, write, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(write, CancellationToken.None);
        // A name can refer to more than one retained catalog identity, as after drop/recreate.
        var recreate = await fixture.Begin();
        var replacement = await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(),
            node.Id, node.Id, type, Properties("x", 3)), Guid.NewGuid(), recreate, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(recreate, CancellationToken.None);
        var current = await fixture.Begin();
        var reopened = GraphStore.Open(fixture.Storage, fixture.Coordinator);

        // Assert: union ranges are sorted and unique, self-loops appear once, old values remain.
        long before = reopened.RelationshipTypeEntriesVisited;
        var found = reopened.ScanRelationshipTypes([type, type, "Missing"], current.Snapshot, CancellationToken.None);
        found.Select(edge => edge.Id).ShouldBe(edges.Where(edge => edge.Id != edges[1].Id).Select(edge => edge.Id).Append(replacement.Id));
        found[0].Properties["x"].ShouldBe(2);
        (reopened.RelationshipTypeEntriesVisited - before).ShouldBe(240);
        reopened.ScanRelationshipTypes([type], old.Snapshot, CancellationToken.None).Select(edge => edge.Id).ShouldBe(edges.Select(edge => edge.Id));
        reopened.ScanRelationshipTypes([type], old.Snapshot, CancellationToken.None)[0].Properties["x"].ShouldBe(0);
        fixture.Coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(0);
        await fixture.Coordinator.RollbackAsync(old, CancellationToken.None);
        await fixture.Coordinator.RollbackAsync(current, CancellationToken.None);
        fixture.Coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBeGreaterThan(0);
        var latest = await fixture.Begin();
        await reopened.PruneIndexesAsync(fixture.Coordinator.PruneBound, CancellationToken.None);
        reopened.ScanRelationshipTypes([type], latest.Snapshot, CancellationToken.None).Select(edge => edge.Id).ShouldBe(found.Select(edge => edge.Id));
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Type postings: uncommitted creation, rollback and detach deletion preserve visibility (#1633)")]
    public async Task ScanRelationshipTypes_CreateAndDetachRollback_ShouldRestorePostingsAsync()
    {
        // Arrange
        await using var fixture = new Fixture();
        var seed = await fixture.Begin();
        var a = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), [], _empty), TestLabelIds.All, seed, CancellationToken.None);
        var b = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), [], _empty), TestLabelIds.All, seed, CancellationToken.None);
        Guid type = Guid.NewGuid();
        var edge = await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), a.Id, b.Id, "R", Properties("x", 0)), type, seed, CancellationToken.None);
        var self = await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), a.Id, a.Id, "S", _empty), Guid.NewGuid(), seed, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(seed, CancellationToken.None);
        var old = await fixture.Begin();
        var writer = await fixture.Begin();

        // Act / Assert
        var partial = await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), b.Id, b.Id, "R", _empty), type, writer, CancellationToken.None);
        fixture.Store.ScanRelationshipTypes(["R"], writer.Snapshot, CancellationToken.None).Select(item => item.Id).ShouldBe([edge.Id, partial.Id]);
        fixture.Store.ScanRelationshipTypes(["R"], old.Snapshot, CancellationToken.None).ShouldHaveSingleItem().Id.ShouldBe(edge.Id);
        await fixture.Store.UpdateRelationshipAsync(edge.Id, Properties("x", 1), writer, CancellationToken.None);
        await fixture.Store.DeleteNodeAsync(a.Id, true, writer, CancellationToken.None);
        fixture.Store.ScanRelationshipTypes(["R", "S"], writer.Snapshot, CancellationToken.None).ShouldHaveSingleItem().Id.ShouldBe(partial.Id);
        await fixture.Coordinator.RollbackAsync(writer, CancellationToken.None);
        var current = await fixture.Begin();
        fixture.Store.ScanRelationshipTypes(["R", "S"], current.Snapshot, CancellationToken.None).Select(item => item.Id).ShouldBe([edge.Id, self.Id]);
        fixture.Store.ScanRelationshipTypes(["R"], current.Snapshot, CancellationToken.None).Single().Properties["x"].ShouldBe(0);
        await fixture.Coordinator.RollbackAsync(current, CancellationToken.None);
        var remove = await fixture.Begin();
        await fixture.Store.DeleteNodeAsync(a.Id, true, remove, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(remove, CancellationToken.None);
        fixture.Store.ScanRelationshipTypes(["R", "S"], old.Snapshot, CancellationToken.None).Select(item => item.Id).ShouldBe([edge.Id, self.Id]);
        var after = await fixture.Begin();
        fixture.Store.ScanRelationshipTypes(["R", "S"], after.Snapshot, CancellationToken.None).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Type postings: recovery retains committed versions and scrubs partial create, update and delete (#1633)")]
    public async Task ScanRelationshipTypes_CrashImage_ShouldRecoverCommittedVersionsAsync()
    {
        // Arrange: copy live files before coordinator disposal, including an unfinished writer.
        var data = new MemoryStream();
        var journal = new MemoryStream();
        using var storage = GraphStorage.Create(data, journal, new MemoryStream(), "type-recovery");
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var store = GraphStore.Open(storage, coordinator);
        var seed = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var node = await store.CreateNodeAsync(new StoredGraphNode((ulong)storage.ReserveTransactionSequence(), [], _empty), TestLabelIds.All, seed, CancellationToken.None);
        Guid type = Guid.NewGuid();
        var edges = new List<StoredGraphRelationship>();
        for (int i = 0; i < 4; i++)
        {
            edges.Add(await store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)storage.ReserveTransactionSequence(), node.Id, node.Id,
                "R", Properties("x", 0)), type, seed, CancellationToken.None));
        }
        await coordinator.CommitAsync(seed, CancellationToken.None);
        var committed = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await store.UpdateRelationshipAsync(edges[0].Id, Properties("x", 1), committed, CancellationToken.None);
        await store.DeleteRelationshipAsync(edges[1].Id, committed, CancellationToken.None);
        await coordinator.CommitAsync(committed, CancellationToken.None);
        var partial = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await store.UpdateRelationshipAsync(edges[0].Id, Properties("x", 2), partial, CancellationToken.None);
        await store.DeleteRelationshipAsync(edges[2].Id, partial, CancellationToken.None);
        var created = await store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)storage.ReserveTransactionSequence(), node.Id, node.Id, "PARTIAL", _empty), Guid.NewGuid(), partial, CancellationToken.None);
        storage.WriteAheadJournal.Flush(forceDurable: false);

        // Act
        using var recovered = GraphStorage.Open(Clone(data), Clone(journal), new MemoryStream(), checkpointOnOpen: false);
        await using var recovery = new TransactionCoordinator(recovered, recovered.WriteAheadJournal, recovered.Records);
        GraphStore.EnsureIndexFormat(recovered);
        var plan = recovery.AnalyzeAndScrub();
        plan.Aborted.ShouldContain(partial.Sequence);
        var reopened = GraphStore.Open(recovered, recovery);
        await reopened.RecoverIndexesAsync(plan.Aborted, CancellationToken.None);
        recovery.CompleteRecovery();
        var reader = await recovery.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);

        // Assert: exact record identities and properties, including restored deleters.
        var found = reopened.ScanRelationshipTypes(["R", "PARTIAL"], reader.Snapshot, CancellationToken.None);
        found.Select(edge => edge.Id).ShouldBe([edges[0].Id, edges[2].Id, edges[3].Id]);
        found.Select(edge => edge.Properties["x"]).ShouldBe(new object?[] { 1, 0, 0 });
        reopened.FindRelationship(created.Id, reader.Snapshot).ShouldBeNull();
        reopened.ScanRelationshipTypes(["PARTIAL"], reader.Snapshot, CancellationToken.None).ShouldBeEmpty();
        await coordinator.RollbackAsync(partial, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Type postings: interrupted create, replacement and deletion undo the entire statement (#1633)")]
    [InlineData("create", 1, false)]
    [InlineData("create", 1, true)]
    [InlineData("update", 1, false)]
    [InlineData("update", 1, true)]
    [InlineData("update", 2, false)]
    [InlineData("update", 2, true)]
    [InlineData("delete", 1, false)]
    [InlineData("delete", 1, true)]
    [InlineData("detach", 1, false)]
    [InlineData("detach", 1, true)]
    public async Task Mutate_InterruptedTypePosting_ShouldRestoreRecordAndTreesAsync(string operation, int interruptAt, bool cancel)
    {
        // Arrange
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), [], _empty), TestLabelIds.All, writer, CancellationToken.None);
        Guid type = Guid.NewGuid();
        var edge = await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), node.Id, node.Id, "R", Properties("x", 0)), type, writer, CancellationToken.None);
        ulong nextId = (ulong)fixture.Storage.ReserveTransactionSequence();
        using var cancellation = new CancellationTokenSource();
        int changed = 0;
        fixture.Store.OnRelationshipTypeIndexChanged = () =>
        {
            if (++changed != interruptAt) { return; }
            if (cancel) { cancellation.Cancel(); }
            else { throw new InvalidOperationException(); }
        };

        // Act
        if (cancel) { await Should.ThrowAsync<OperationCanceledException>(() => MutateAsync(cancellation.Token)); }
        else { await Should.ThrowAsync<InvalidOperationException>(() => MutateAsync(CancellationToken.None)); }
        fixture.Store.OnRelationshipTypeIndexChanged = null;

        // Assert: the failed statement leaves both the posting and the adjacency usable.
        changed.ShouldBe(interruptAt);
        fixture.Store.ScanRelationshipTypes(["R", "NEW"], writer.Snapshot, CancellationToken.None).ShouldHaveSingleItem().Properties["x"].ShouldBe(0);
        fixture.Store.FindRelationship(nextId, writer.Snapshot).ShouldBeNull();
        (await fixture.Store.GetIncidentAsync(node.Id, writer.Snapshot, CancellationToken.None)).ShouldHaveSingleItem().Id.ShouldBe(edge.Id);
        await MutateAsync(CancellationToken.None);
        await fixture.Coordinator.CommitAsync(writer, CancellationToken.None);
        var current = await fixture.Begin();
        var found = fixture.Store.ScanRelationshipTypes(["R", "NEW"], current.Snapshot, CancellationToken.None);
        found.Count.ShouldBe(operation == "create" ? 2 : operation == "update" ? 1 : 0);
        if (operation == "update") { found.Single().Properties["x"].ShouldBe(1); }

        async Task MutateAsync(CancellationToken token)
        {
            switch (operation)
            {
                case "create":
                    await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship(nextId, node.Id, node.Id, "NEW", _empty), Guid.NewGuid(), writer, token);
                    break;
                case "update": await fixture.Store.UpdateRelationshipAsync(edge.Id, Properties("x", 1), writer, token); break;
                case "delete": await fixture.Store.DeleteRelationshipAsync(edge.Id, writer, token); break;
                case "detach": await fixture.Store.DeleteNodeAsync(node.Id, true, writer, token); break;
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Storage] - Type postings: failed first creation rolls back new tree registrations (#1633)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateRelationshipAsync_FirstPostingFailure_ShouldPermitRetryAsync(bool cancel)
    {
        // Arrange
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), [], _empty), TestLabelIds.All, writer, CancellationToken.None);
        var edge = new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), node.Id, node.Id, "FIRST", _empty);
        Guid type = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        int changed = 0;
        fixture.Store.OnRelationshipTypeIndexChanged = () =>
        {
            changed++;
            if (cancel) { cancellation.Cancel(); }
            else { throw new InvalidOperationException(); }
        };

        // Act
        if (cancel)
        { await Should.ThrowAsync<OperationCanceledException>(() => fixture.Store.CreateRelationshipAsync(edge, type, writer, cancellation.Token).AsTask()); }
        else
        { await Should.ThrowAsync<InvalidOperationException>(() => fixture.Store.CreateRelationshipAsync(edge, type, writer, CancellationToken.None).AsTask()); }
        fixture.Store.OnRelationshipTypeIndexChanged = null;

        // Assert: a failed first tree never publishes its registration or name/token directory.
        changed.ShouldBe(1);
        fixture.Store.ScanRelationshipTypes(["FIRST"], writer.Snapshot, CancellationToken.None).ShouldBeEmpty();
        using (var registrations = fixture.Storage.GetUnitIterator(1)) { registrations.MoveNext().ShouldBeFalse(); }
        await fixture.Store.CreateRelationshipAsync(edge, type, writer, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(writer, CancellationToken.None);
        var reader = await fixture.Begin();
        GraphStore.Open(fixture.Storage, fixture.Coordinator).ScanRelationshipTypes(["FIRST"], reader.Snapshot, CancellationToken.None)
            .ShouldHaveSingleItem().Id.ShouldBe(edge.Id);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Type postings: missing mandatory registration refuses open (#1633)")]
    public async Task Open_MissingRelationshipTypeRegistration_ShouldRefuseAsync()
    {
        // Arrange
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), [], _empty), TestLabelIds.All, writer, CancellationToken.None);
        await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), node.Id, node.Id, "R", _empty), Guid.NewGuid(), writer, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(writer, CancellationToken.None);
        using var registrations = fixture.Storage.GetUnitIterator(1);
        bool changed = false;
        while (registrations.MoveNext())
        {
            var registration = registrations.Current;
            if (BinaryPrimitives.ReadUInt64LittleEndian(registration.Data.Span.Slice(18)) != ulong.MaxValue - 2) { continue; }
            byte[] bytes = registration.Data.ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(18), 123);
            // Act: leave the tree readable but remove its required registration.
            using var bracket = fixture.Storage.BeginTransaction();
            fixture.Storage.UpdateEntry(bracket, registration.PageId, registration.SlotIndex, bytes);
            bracket.Commit();
            changed = true;
            break;
        }

        // Assert
        changed.ShouldBeTrue();
        Should.Throw<StorageCorruptionException>(() => GraphStore.Open(fixture.Storage, fixture.Coordinator));
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Storage] - Type postings: empty, canceled and abandoned streams close cursors (#1633)")]
    public async Task StreamRelationshipTypes_EarlyExits_ShouldLeaveStoreUsableAsync()
    {
        // Arrange
        await using var fixture = new Fixture();
        var writer = await fixture.Begin();
        var node = await fixture.Store.CreateNodeAsync(new StoredGraphNode((ulong)fixture.Storage.ReserveTransactionSequence(), [], _empty), TestLabelIds.All, writer, CancellationToken.None);
        for (int i = 0; i < 2; i++)
        {
            await fixture.Store.CreateRelationshipAsync(new StoredGraphRelationship((ulong)fixture.Storage.ReserveTransactionSequence(), node.Id, node.Id, "R", _empty), Guid.Parse("10000000-0000-0000-0000-000000000001"), writer, CancellationToken.None);
        }
        using var cancellation = new CancellationTokenSource();
        fixture.Store.OnRelationshipTypeEntryRead = cancellation.Cancel;

        // Act / Assert
        Should.Throw<OperationCanceledException>(() => fixture.Store.ScanRelationshipTypes(["R"], writer.Snapshot, cancellation.Token));
        fixture.Store.OnRelationshipTypeEntryRead = null;
        Should.Throw<OperationCanceledException>(() => fixture.Store.ScanRelationshipTypes(["Missing"], writer.Snapshot, cancellation.Token));
        Should.Throw<OperationCanceledException>(() => fixture.Store.ScanRelationshipTypes([], writer.Snapshot, cancellation.Token));
        fixture.Store.OnRelationshipTypeEntryRead = () => throw new InvalidOperationException();
        Should.Throw<InvalidOperationException>(() => fixture.Store.ScanRelationshipTypes(["R"], writer.Snapshot, CancellationToken.None));
        fixture.Store.OnRelationshipTypeEntryRead = null;
        fixture.Store.ScanRelationshipTypes(["Missing"], writer.Snapshot, CancellationToken.None).ShouldBeEmpty();
        fixture.Store.ScanRelationshipTypes(["r"], writer.Snapshot, CancellationToken.None).ShouldBeEmpty();
        fixture.Store.StreamRelationshipTypes(["R"], writer.Snapshot, CancellationToken.None).Take(1).ShouldHaveSingleItem();
        await fixture.Store.DeleteNodeAsync(node.Id, true, writer, CancellationToken.None);
        await fixture.Coordinator.CommitAsync(writer, CancellationToken.None);
        var current = await fixture.Begin();
        fixture.Store.ScanRelationshipTypes(["R"], current.Snapshot, CancellationToken.None).ShouldBeEmpty();
    }
}
