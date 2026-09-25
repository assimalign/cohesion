using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Catalog.Tests;

public sealed class GraphCatalogTests
{
    [Fact]
    public async Task Discovery_ShouldPersistDistinctLabelsTypesPropertiesAndIndexes()
    {
        await using var database = new TestDatabase();
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var person = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        var friend = new GraphRelationshipTypeMetadata(Guid.NewGuid(), "FRIEND");
        var property = new GraphPropertyKeyMetadata(person.Id, "name", DatabaseType.String, Required: true);
        var index = new GraphIndexMetadata(person.Id, "by-name", "name");
        await database.Catalog.SaveLabelAsync(new GraphLabelMetadata(Guid.NewGuid(), "Animal"), context);
        await database.Catalog.SaveLabelAsync(person, context);
        await database.Catalog.SaveRelationshipTypeAsync(friend, context);
        await database.Catalog.SavePropertyKeyAsync(property, context);
        await database.Catalog.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(friend.Id, "since", DatabaseType.Int64), context);
        await database.Catalog.SaveIndexAsync(index, context);
        await database.Coordinator.CommitAsync(context);

        var reopened = GraphCatalog.Open(database.Storage, database.Coordinator);
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        reopened.GetLabels(reader.Snapshot).Select(item => item.Name).ShouldBe(new[] { "Animal", "Person" });
        reopened.FindLabel("person", reader.Snapshot).ShouldBeNull();
        reopened.FindLabel("Person", reader.Snapshot).ShouldBe(person);
        reopened.GetRelationshipTypes(reader.Snapshot).ShouldHaveSingleItem().ShouldBe(friend);
        reopened.GetPropertyKeys(person.Id, reader.Snapshot).ShouldHaveSingleItem().ShouldBe(property);
        reopened.GetPropertyKeys(friend.Id, reader.Snapshot).ShouldHaveSingleItem().Name.ShouldBe("since");
        reopened.GetIndexes(person.Id, reader.Snapshot).ShouldHaveSingleItem().ShouldBe(index);
        await database.Coordinator.RollbackAsync(reader);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SchemaOwnership_ShouldRefuseAlterDropAndChildChanges(bool relationship)
    {
        await using var database = new TestDatabase();
        var id = Guid.NewGuid();
        const string name = "Owned";
        const string schema = "PeopleSchema";
        var create = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        if (relationship)
        {
            await database.Catalog.SaveRelationshipTypeAsync(new GraphRelationshipTypeMetadata(id, name, DatabaseObjectOwner.Schema, schema), create);
        }
        else
        {
            await database.Catalog.SaveLabelAsync(new GraphLabelMetadata(id, name, DatabaseObjectOwner.Schema, schema), create);
        }
        await database.Coordinator.CommitAsync(create);
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        string kind = relationship ? "RELATIONSHIP TYPE" : "LABEL";
        var alter = await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
        {
            if (relationship)
            {
                await database.Catalog.SaveRelationshipTypeAsync(new GraphRelationshipTypeMetadata(id, name), writer);
            }
            else
            {
                await database.Catalog.SaveLabelAsync(new GraphLabelMetadata(id, name), writer);
            }
        });
        alter.ObjectName.ShouldBe(name);
        alter.OwningSchema.ShouldBe(schema);
        alter.Operation.ShouldBe("ALTER " + kind);
        var drop = await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
        {
            if (relationship)
            {
                await database.Catalog.DeleteRelationshipTypeAsync(id, writer);
            }
            else
            {
                await database.Catalog.DeleteLabelAsync(id, writer);
            }
        });
        drop.Operation.ShouldBe("DROP " + kind);
        drop.Message.ShouldContain(name);
        drop.Message.ShouldContain(schema);
        await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
            await database.Catalog.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(id, "name"), writer));
        if (!relationship)
        {
            await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
                await database.Catalog.SaveIndexAsync(new GraphIndexMetadata(id, "by-name", "name"), writer));
        }
        await database.Coordinator.RollbackAsync(writer);
    }

    [Fact]
    public async Task DefinitionDrop_ShouldAtomicallyHideChildrenAndRollbackAllMetadata()
    {
        await using var database = new TestDatabase();
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        var create = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveLabelAsync(label, create);
        await database.Catalog.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(label.Id, "name"), create);
        await database.Catalog.SaveIndexAsync(new GraphIndexMetadata(label.Id, "name-index", "name"), create);
        await database.Coordinator.CommitAsync(create);
        var oldReader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.DeleteLabelAsync(label.Id, writer);
        database.Catalog.FindLabel(label.Name, writer.Snapshot).ShouldBeNull();
        database.Catalog.GetPropertyKeys(label.Id, writer.Snapshot).ShouldBeEmpty();
        database.Catalog.GetIndexes(label.Id, writer.Snapshot).ShouldBeEmpty();
        database.Catalog.FindLabel(label.Name, oldReader.Snapshot).ShouldBe(label);
        database.Catalog.GetIndexes(label.Id, oldReader.Snapshot).ShouldHaveSingleItem();
        await database.Coordinator.RollbackAsync(writer);
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        database.Catalog.FindLabel(label.Name, reader.Snapshot).ShouldBe(label);
        database.Catalog.GetPropertyKeys(label.Id, reader.Snapshot).ShouldHaveSingleItem();
        database.Catalog.GetIndexes(label.Id, reader.Snapshot).ShouldHaveSingleItem();
        await database.Coordinator.RollbackAsync(reader);
        await database.Coordinator.RollbackAsync(oldReader);
    }

    [Fact]
    public async Task DefinitionIdentity_ShouldRejectRenamesDuplicatesAndMissingParents()
    {
        await using var database = new TestDatabase();
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveLabelAsync(label, writer);
        await Should.ThrowAsync<GraphCatalogException>(async () =>
            await database.Catalog.SaveLabelAsync(label with { Id = Guid.NewGuid() }, writer));
        await Should.ThrowAsync<GraphCatalogException>(async () =>
            await database.Catalog.SaveLabelAsync(label with { Name = "Renamed" }, writer));
        await Should.ThrowAsync<GraphCatalogException>(async () =>
            await database.Catalog.SaveRelationshipTypeAsync(new GraphRelationshipTypeMetadata(label.Id, "TYPE"), writer));
        await Should.ThrowAsync<GraphCatalogException>(async () =>
            await database.Catalog.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(Guid.NewGuid(), "name"), writer));
        await database.Catalog.SaveIndexAsync(new GraphIndexMetadata(label.Id, "index", "name"), writer);
        await Should.ThrowAsync<GraphCatalogException>(async () =>
            await database.Catalog.SaveIndexAsync(new GraphIndexMetadata(label.Id, "index", "age"), writer));
        await database.Coordinator.RollbackAsync(writer);
    }

    [Fact]
    public async Task CrashRecovery_ShouldRetainCommittedDefinitionsAndScrubPartialChanges()
    {
        await using var database = new TestDatabase();
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        var type = new GraphRelationshipTypeMetadata(Guid.NewGuid(), "FRIEND", DatabaseObjectOwner.Schema, "People");
        var create = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveLabelAsync(label, create);
        await database.Catalog.SaveRelationshipTypeAsync(type, create);
        await database.Catalog.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(label.Id, "name", DatabaseType.String), create);
        await database.Catalog.SaveIndexAsync(new GraphIndexMetadata(label.Id, "by-name", "name"), create);
        await database.Coordinator.CommitAsync(create);

        var partial = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.DeleteLabelAsync(label.Id, partial);
        await database.Catalog.SaveLabelAsync(new GraphLabelMetadata(Guid.NewGuid(), "Partial"), partial);
        database.Storage.FlushPendingCommits();
        database.Storage.WriteBackDirtyPages(int.MaxValue);

        using var storage = GraphStorage.Open(Copy(database.Data), Copy(database.Journal), new MemoryStream(), checkpointOnOpen: false);
        await using var recovered = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        recovered.AnalyzeAndScrub();
        var catalog = GraphCatalog.Open(storage, recovered);
        recovered.CompleteRecovery();
        var reader = await recovered.BeginAsync(IsolationLevel.Snapshot);
        catalog.GetLabels(reader.Snapshot).ShouldHaveSingleItem().ShouldBe(label);
        catalog.FindRelationshipType(type.Name, reader.Snapshot).ShouldBe(type);
        catalog.GetPropertyKeys(label.Id, reader.Snapshot).ShouldHaveSingleItem().Name.ShouldBe("name");
        catalog.GetIndexes(label.Id, reader.Snapshot).ShouldHaveSingleItem().Name.ShouldBe("by-name");
        await recovered.RollbackAsync(reader);
        await database.Coordinator.RollbackAsync(partial);
    }

    [Fact]
    public async Task WriterWaitingForDefinitionLock_ShouldRejectAConcurrentFirstDefinition()
    {
        await using var database = new TestDatabase();
        var first = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var waiting = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Coordinator.LockManager.AcquireAsync(first.Sequence, LockResource.Database(), LockMode.Exclusive);
        var wait = database.Coordinator.LockManager.AcquireAsync(waiting.Sequence, LockResource.Database(), LockMode.Exclusive).AsTask();
        wait.IsCompleted.ShouldBeFalse();
        var original = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        await database.Catalog.SaveLabelAsync(original, first);
        await database.Coordinator.CommitAsync(first);
        await wait;
        var error = await Should.ThrowAsync<GraphCatalogException>(async () =>
            await database.Catalog.SaveLabelAsync(new GraphLabelMetadata(Guid.NewGuid(), "Person"), waiting));
        error.Message.ShouldContain("write conflict");
        await database.Coordinator.RollbackAsync(waiting);
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        database.Catalog.GetLabels(reader.Snapshot).ShouldHaveSingleItem().ShouldBe(original);
        await database.Coordinator.RollbackAsync(reader);
    }

    [Theory]
    [InlineData("label")]
    [InlineData("type")]
    [InlineData("property-save")]
    [InlineData("property-delete")]
    [InlineData("index-save")]
    [InlineData("index-delete")]
    [InlineData("parent-drop")]
    [InlineData("parent-deleted")]
    public async Task StaleSnapshot_ShouldRefuseMetadataOverwriteOrOrphanedChild(string operation)
    {
        await using var database = new TestDatabase();
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        var type = new GraphRelationshipTypeMetadata(Guid.NewGuid(), "FRIEND");
        var property = new GraphPropertyKeyMetadata(label.Id, "name");
        var index = new GraphIndexMetadata(label.Id, "by-name", "name");
        var create = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveLabelAsync(label, create);
        await database.Catalog.SaveRelationshipTypeAsync(type, create);
        await database.Catalog.SavePropertyKeyAsync(property, create);
        await database.Catalog.SaveIndexAsync(index, create);
        await database.Coordinator.CommitAsync(create);
        var stale = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        switch (operation)
        {
            case "label": await database.Catalog.SaveLabelAsync(label, writer); break;
            case "type": await database.Catalog.SaveRelationshipTypeAsync(type, writer); break;
            case "property-save":
            case "property-delete": await database.Catalog.SavePropertyKeyAsync(property with { Required = true }, writer); break;
            case "index-save":
            case "index-delete": await database.Catalog.SaveIndexAsync(index, writer); break;
            case "parent-drop": await database.Catalog.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(label.Id, "age"), writer); break;
            case "parent-deleted": await database.Catalog.DeleteLabelAsync(label.Id, writer); break;
        }
        await database.Coordinator.CommitAsync(writer);
        await Should.ThrowAsync<GraphCatalogException>(async () =>
        {
            switch (operation)
            {
                case "label": await database.Catalog.SaveLabelAsync(label, stale); break;
                case "type": await database.Catalog.DeleteRelationshipTypeAsync(type.Id, stale); break;
                case "property-save": await database.Catalog.SavePropertyKeyAsync(property, stale); break;
                case "property-delete": await database.Catalog.DeletePropertyKeyAsync(label.Id, "name", stale); break;
                case "index-save": await database.Catalog.SaveIndexAsync(index, stale); break;
                case "index-delete": await database.Catalog.DeleteIndexAsync(label.Id, "by-name", stale); break;
                case "parent-drop": await database.Catalog.DeleteLabelAsync(label.Id, stale); break;
                case "parent-deleted": await database.Catalog.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(label.Id, "age"), stale); break;
            }
        });
        await database.Coordinator.RollbackAsync(stale);
    }

    [Fact]
    public async Task StaleSnapshot_ShouldHonorNewlyMarkedSchemaOwnership()
    {
        await using var database = new TestDatabase();
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        var create = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveLabelAsync(label, create);
        await database.Coordinator.CommitAsync(create);
        var stale = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var marker = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveLabelAsync(label with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "People" }, marker);
        await database.Coordinator.CommitAsync(marker);
        var error = await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
            await database.Catalog.SaveLabelAsync(label, stale));
        error.OwningSchema.ShouldBe("People");
        await Should.ThrowAsync<DatabaseObjectLockedException>(async () =>
            await database.Catalog.SavePropertyKeyAsync(new GraphPropertyKeyMetadata(label.Id, "age"), stale));
        await database.Coordinator.RollbackAsync(stale);
    }

    [Fact]
    public async Task RollbackOfOnlyDefinition_ShouldForgetFreedPageAndAllowSameNameToBeRecreated()
    {
        await using var database = new TestDatabase();
        var first = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var aborted = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        await database.Catalog.SaveLabelAsync(aborted, first);
        await database.Coordinator.RollbackAsync(first);
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        database.Catalog.FindLabel("Person", reader.Snapshot).ShouldBeNull();
        database.Catalog.GetLabels(reader.Snapshot).ShouldBeEmpty();
        await database.Coordinator.RollbackAsync(reader);
        var next = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        var replacement = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        await database.Catalog.SaveLabelAsync(replacement, next);
        await database.Coordinator.CommitAsync(next);
        var fresh = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        database.Catalog.GetLabels(fresh.Snapshot).ShouldHaveSingleItem().ShouldBe(replacement);
        await database.Coordinator.RollbackAsync(fresh);
    }

    [Fact]
    public async Task Open_ShouldRejectUnsupportedMetadata()
    {
        await using var database = new TestDatabase();
        byte[] bytes = new byte[34];
        bytes[16] = 1;
        bytes[17] = 255;
        using var bracket = database.Storage.BeginTransaction();
        database.Storage.InsertEntry(bracket, bytes);
        bracket.Commit();
        Should.Throw<GraphCatalogException>(() => GraphCatalog.Open(database.Storage, database.Coordinator));
    }

    [Fact]
    public async Task Read_ShouldSurfaceCorruptMetadataInsteadOfReportingAbsence()
    {
        await using var database = new TestDatabase();
        var writer = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        await database.Catalog.SaveLabelAsync(new GraphLabelMetadata(Guid.NewGuid(), "Person"), writer);
        await database.Coordinator.CommitAsync(writer);
        using (var iterator = database.Storage.GetUnitIterator(0))
        {
            iterator.MoveNext().ShouldBeTrue();
            var unit = iterator.Current;
            byte[] bytes = unit.Data.ToArray();
            bytes[16] = 99;
            using var bracket = database.Storage.BeginTransaction();
            database.Storage.UpdateEntry(bracket, unit.PageId, unit.SlotIndex, bytes);
            bracket.Commit();
        }
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot);
        Should.Throw<GraphCatalogException>(() => database.Catalog.FindLabel("Person", reader.Snapshot));
        await database.Coordinator.RollbackAsync(reader);
    }

    private static MemoryStream Copy(MemoryStream source)
    {
        var copy = new MemoryStream();
        copy.Write(source.ToArray());
        copy.Position = 0;
        return copy;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        internal MemoryStream Data { get; } = new();
        internal MemoryStream Journal { get; } = new();
        internal GraphStorage Storage { get; }
        internal TransactionCoordinator Coordinator { get; }
        internal IGraphCatalog Catalog { get; }

        internal TestDatabase()
        {
            Storage = GraphStorage.Create(Data, Journal, new MemoryStream(), "graph-catalog-tests");
            Coordinator = new TransactionCoordinator(Storage, Storage.WriteAheadJournal, Storage.Records);
            Catalog = GraphCatalog.Open(Storage, Coordinator);
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Storage.DisposeAsync();
        }
    }
}
