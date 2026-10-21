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
