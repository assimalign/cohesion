using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphCatalogIdentityTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Catalog: reused base identities retain live routing through reopen and purge")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindById_BaseReusedIdentity_ShouldRetainLiveNameAfterPurge(bool tombstoneLast)
    {
        // Arrange: independent catalog instances reproduce the base API's two-name state,
        // without bypassing catalog encoding or writing raw storage records.
        await using var engine = GraphDatabaseEngine.Create("catalog-reused-id", new()
        { MaintenanceInterval = TimeSpan.FromHours(1), CheckpointInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var coordinator = database.Coordinator;
        var first = database.Catalog;
        var baseWriter = GraphCatalog.Open(database.DataStorage, coordinator);
        var a = new GraphLabelMetadata(Guid.NewGuid(), "a");
        var b = a with { Name = "b" };
        if (tombstoneLast)
        {
            var earlier = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
            await baseWriter.SaveLabelAsync(b, earlier, CancellationToken.None);
            await coordinator.CommitAsync(earlier, CancellationToken.None);
        }
        var create = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await first.SaveLabelAsync(a, create, CancellationToken.None);
        await coordinator.CommitAsync(create, CancellationToken.None);
        var drop = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await first.DeleteLabelAsync(a.Id, drop, CancellationToken.None);
        await coordinator.CommitAsync(drop, CancellationToken.None);
        if (!tombstoneLast)
        {
            var replace = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
            await baseWriter.SaveLabelAsync(b, replace, CancellationToken.None);
            await coordinator.CommitAsync(replace, CancellationToken.None);
        }
        var reopened = GraphCatalog.Open(database.DataStorage, coordinator);
        var read = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        reopened.FindLabel(a.Id, read.Snapshot).ShouldBe(b);
        await coordinator.RollbackAsync(read, CancellationToken.None);

        // Act: purge a's tombstone, then prune it through the identity lookup and mutate b.
        coordinator.RunVersionPurgePass(CancellationToken.None);
        var update = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        reopened.FindLabel(b.Id, update.Snapshot).ShouldBe(b);
        await reopened.SaveLabelAsync(b, update, CancellationToken.None);
        await reopened.SavePropertyKeyAsync(new(b.Id, "live"), update, CancellationToken.None);
        await coordinator.CommitAsync(update, CancellationToken.None);

        // Assert
        var verify = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        reopened.FindLabel("a", verify.Snapshot).ShouldBeNull();
        reopened.FindLabel(b.Id, verify.Snapshot).ShouldBe(b);
        reopened.GetPropertyKeys(b.Id, verify.Snapshot).ShouldHaveSingleItem().Name.ShouldBe("live");
        await coordinator.RollbackAsync(verify, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Catalog: dropped identities wait for purge before reuse")]
    public async Task SaveLabelAsync_DroppedIdentity_ShouldRefuseReuseUntilPurge()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("catalog-id-reuse", new()
        { MaintenanceInterval = TimeSpan.FromHours(1) });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var catalog = database.Catalog;
        var coordinator = database.Coordinator;
        var a = new GraphLabelMetadata(Guid.NewGuid(), "a");
        var create = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await catalog.SaveLabelAsync(a, create, CancellationToken.None);
        await coordinator.CommitAsync(create, CancellationToken.None);
        var drop = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await catalog.DeleteLabelAsync(a.Id, drop, CancellationToken.None);
        await coordinator.CommitAsync(drop, CancellationToken.None);
        var before = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);

        // Act
        var refused = await Should.ThrowAsync<GraphCatalogException>(async () =>
            await catalog.SaveLabelAsync(a with { Name = "b" }, before, CancellationToken.None));
        await coordinator.RollbackAsync(before, CancellationToken.None);
        coordinator.RunVersionPurgePass(CancellationToken.None);
        var after = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await catalog.SaveLabelAsync(a with { Name = "b" }, after, CancellationToken.None);

        // Assert
        refused.IsConflict.ShouldBeFalse();
        refused.IsCallerError.ShouldBeTrue();
        catalog.FindLabel(a.Id, after.Snapshot)!.Value.Name.ShouldBe("b");
        await coordinator.RollbackAsync(after, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Catalog: identity lookups retain MVCC through drop, recreation, rollback and reopen")]
    public async Task FindById_NameRecreated_ShouldRespectSnapshotsAndKinds()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("catalog-identity", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var catalog = database.Catalog;
        var coordinator = database.Coordinator;
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Same");
        var type = new GraphRelationshipTypeMetadata(Guid.NewGuid(), "Same");
        var create = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await catalog.SaveLabelAsync(label, create, CancellationToken.None);
        await catalog.SaveRelationshipTypeAsync(type, create, CancellationToken.None);
        await catalog.SavePropertyKeyAsync(new(label.Id, "old"), create, CancellationToken.None);
        await catalog.SaveIndexAsync(new(label.Id, "old_index", "old"), create, CancellationToken.None);
        await coordinator.CommitAsync(create, CancellationToken.None);
        var old = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var replace = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var replacement = new GraphLabelMetadata(Guid.NewGuid(), label.Name);

        // Act
        await catalog.DeleteLabelAsync(label.Id, replace, CancellationToken.None);
        await catalog.SaveLabelAsync(replacement, replace, CancellationToken.None);
        await catalog.SavePropertyKeyAsync(new(replacement.Id, "new"), replace, CancellationToken.None);
        await coordinator.CommitAsync(replace, CancellationToken.None);
        var fresh = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var reopened = GraphCatalog.Open(database.DataStorage, coordinator);

        // Assert
        foreach (var view in new[] { catalog, reopened })
        {
            view.FindLabel(label.Id, old.Snapshot).ShouldBe(label);
            view.FindLabel(replacement.Id, old.Snapshot).ShouldBeNull();
            view.FindLabel(label.Id, fresh.Snapshot).ShouldBeNull();
            view.FindLabel(replacement.Id, fresh.Snapshot).ShouldBe(replacement);
            view.FindLabel(type.Id, fresh.Snapshot).ShouldBeNull();
            view.FindRelationshipType(label.Id, fresh.Snapshot).ShouldBeNull();
            view.FindRelationshipType(type.Id, old.Snapshot).ShouldBe(type);
            view.GetPropertyKeys(label.Id, old.Snapshot).ShouldHaveSingleItem().Name.ShouldBe("old");
            view.GetIndexes(label.Id, old.Snapshot).ShouldHaveSingleItem().Name.ShouldBe("old_index");
            view.GetPropertyKeys(label.Id, fresh.Snapshot).ShouldBeEmpty();
            view.GetIndexes(label.Id, fresh.Snapshot).ShouldBeEmpty();
            view.GetPropertyKeys(replacement.Id, fresh.Snapshot).ShouldHaveSingleItem().Name.ShouldBe("new");
        }
        await coordinator.RollbackAsync(fresh, CancellationToken.None);
        await coordinator.RollbackAsync(old, CancellationToken.None);

        // A reverted child/definition is removed lazily from both routing maps, then may be recreated.
        var rollback = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var transient = new GraphLabelMetadata(Guid.NewGuid(), "Transient");
        await catalog.SaveLabelAsync(transient, rollback, CancellationToken.None);
        await catalog.SavePropertyKeyAsync(new(transient.Id, "p"), rollback, CancellationToken.None);
        await catalog.SaveIndexAsync(new(transient.Id, "i", "p"), rollback, CancellationToken.None);
        await coordinator.RollbackAsync(rollback, CancellationToken.None);
        var retry = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        catalog.FindLabel(transient.Id, retry.Snapshot).ShouldBeNull();
        catalog.GetPropertyKeys(transient.Id, retry.Snapshot).ShouldBeEmpty();
        catalog.GetIndexes(transient.Id, retry.Snapshot).ShouldBeEmpty();
        await catalog.SaveLabelAsync(transient, retry, CancellationToken.None);
        await catalog.SavePropertyKeyAsync(new(transient.Id, "p"), retry, CancellationToken.None);
        await catalog.SaveIndexAsync(new(transient.Id, "i", "p"), retry, CancellationToken.None);
        catalog.FindLabel(transient.Id, retry.Snapshot).ShouldBe(transient);
        catalog.GetPropertyKeys(transient.Id, retry.Snapshot).Select(item => item.Name).ShouldBe(["p"]);
        catalog.GetIndexes(transient.Id, retry.Snapshot).Select(item => item.Name).ShouldBe(["i"]);
        await coordinator.RollbackAsync(retry, CancellationToken.None);
    }
}
