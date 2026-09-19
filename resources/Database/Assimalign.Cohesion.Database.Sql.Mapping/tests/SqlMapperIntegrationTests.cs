using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Mapping;
using Assimalign.Cohesion.Database.Sql.Client;

namespace Assimalign.Cohesion.Database.Sql.Mapping.Tests;

/// <summary>Exercises generated relational mappings over the real SQL engine and wire client.</summary>
public sealed class SqlMapperIntegrationTests
{
    /// <summary>Retained names containing spaces and SQL keywords round-trip as delimited identifiers.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Names: quoted table and keyword column support generated CRUD")]
    public async Task SaveChangesAsync_DelimitedIdentifiers_ShouldRoundTripQueryUpdateAndDelete()
    {
        // Arrange.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var work = MappingUnitOfWork.Create(harness.Store);
        var tracked = SqlMapping.Register(work, new MapperQuotedEntityMapper());
        var entity = new MapperQuotedEntity { Id = 5, @select = "before" };
        tracked.Add(entity);

        // Act / Assert: schema deployment and all generated statements retain the exact names.
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(1);
        var query = SqlMapping.Query(new MapperQuotedEntityMapper())
            .Where(MapperQuotedEntityMapper.Columns.@select.Equal("before"))
            .OrderBy(MapperQuotedEntityMapper.Columns.@select);
        (await harness.Store.QueryAsync(query, timeout.Token)).ShouldHaveSingleItem().Id.ShouldBe(5);
        entity.@select = "after";
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(1);
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperQuotedEntityMapper()), timeout.Token))
            .ShouldHaveSingleItem().@select.ShouldBe("after");
        tracked.Remove(entity);
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(1);
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperQuotedEntityMapper()), timeout.Token)).ShouldBeEmpty();
    }

    /// <summary>The same declaration provisions the foreign key and generates both entity mappings.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Graph: generated mappings round-trip a foreign-key graph")]
    public async Task SaveChangesAsync_ForeignKeyGraph_ShouldOrderWritesAndRoundTrip()
    {
        // Arrange: registration order deliberately opposes the foreign-key dependency.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var work = MappingUnitOfWork.Create(harness.Store);
        var children = SqlMapping.Register(work, new MapperChildMapper());
        var parents = SqlMapping.Register(work, new MapperParentMapper());
        parents.Add(new MapperParent { Id = 7, Name = "parent" });
        children.Add(new MapperChild { Id = 70, ParentId = 7, Name = "first", Note = null });
        children.Add(new MapperChild { Id = 71, ParentId = 7, Name = "second", Note = "relationship" });

        // Act.
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(3);
        var loadedParents = await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper())
            .Where(MapperParentMapper.Columns.Id.Equal(7)), timeout.Token);
        var loadedChildren = await harness.Store.QueryAsync(SqlMapping.Query(new MapperChildMapper())
            .Where(MapperChildMapper.Columns.ParentId.Equal(7)).OrderBy(MapperChildMapper.Columns.Id), timeout.Token);

        // Assert: independent row materialization preserves the explicit relationship key.
        var parent = loadedParents.ShouldHaveSingleItem();
        parent.Name.ShouldBe("parent");
        loadedChildren.Select(child => child.ParentId).ShouldAllBe(key => key == parent.Id);
        loadedChildren.Select(child => child.Id).ShouldBe(new[] { 70, 71 });
        loadedChildren[0].Note.ShouldBeNull();
        loadedChildren[1].Note.ShouldBe("relationship");
        children.Attach(loadedChildren[0]).ShouldBeSameAs(children.Find(70));
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(0);
    }

    /// <summary>One save accepts insert, update and delete snapshots together.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Save: mixed insert update and delete commit as one unit")]
    public async Task SaveChangesAsync_MixedChanges_ShouldCommitTogether()
    {
        // Arrange.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var work = MappingUnitOfWork.Create(harness.Store);
        var parents = SqlMapping.Register(work, new MapperParentMapper());
        var children = SqlMapping.Register(work, new MapperChildMapper());
        var parent = new MapperParent { Id = 1, Name = "before" };
        var removed = new MapperChild { Id = 10, ParentId = 1, Name = "remove" };
        parents.Add(parent);
        children.Add(removed);
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(2);
        parent.Name = "after";
        children.Remove(removed);
        children.Add(new MapperChild { Id = 11, ParentId = 1, Name = "insert" });

        // Act.
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(3);

        // Assert.
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper()), timeout.Token))
            .ShouldHaveSingleItem().Name.ShouldBe("after");
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperChildMapper()), timeout.Token))
            .ShouldHaveSingleItem().Id.ShouldBe(11);
        children.Find(10).ShouldBeNull();
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(0);
    }

    /// <summary>A later foreign-key failure rolls back an earlier real engine insert and preserves tracking.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Save: failed mixed changes leave no partial writes and can retry")]
    public async Task SaveChangesAsync_ForeignKeyFailure_ShouldRollbackAndPreserveRetry()
    {
        // Arrange.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var work = MappingUnitOfWork.Create(harness.Store);
        var children = SqlMapping.Register(work, new MapperChildMapper());
        var parents = SqlMapping.Register(work, new MapperParentMapper());
        var existing = new MapperParent { Id = 1, Name = "before" };
        var removed = new MapperChild { Id = 10, ParentId = 1, Name = "keep after rollback" };
        parents.Add(existing);
        children.Add(removed);
        await work.SaveChangesAsync(timeout.Token);
        existing.Name = "after";
        parents.Add(new MapperParent { Id = 2, Name = "new parent staged before invalid child" });
        children.Remove(removed);
        var invalid = new MapperChild { Id = 20, ParentId = 999, Name = "bad foreign key" };
        children.Add(invalid);

        // Act: the parent insert succeeds inside the transaction before the child fails.
        await Should.ThrowAsync<SqlClientException>(async () => await work.SaveChangesAsync(timeout.Token));

        // Assert: all original values survive and the new parent never becomes visible.
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper()), timeout.Token))
            .ShouldHaveSingleItem().Name.ShouldBe("before");
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperChildMapper()), timeout.Token))
            .ShouldHaveSingleItem().Id.ShouldBe(10);
        invalid.ParentId = 2;
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(4);
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper())
            .OrderBy(MapperParentMapper.Columns.Id), timeout.Token)).Select(parent => parent.Name)
            .ShouldBe(new[] { "after", "new parent staged before invalid child" });
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperChildMapper()), timeout.Token))
            .ShouldHaveSingleItem().ParentId.ShouldBe(2);
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(0);
    }

    /// <summary>A final restricted parent deletion rolls back insert, update and child deletion already executed.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Save: late delete failure rolls back every earlier change kind")]
    public async Task SaveChangesAsync_LateRestrictedDelete_ShouldRollbackInsertUpdateAndDelete()
    {
        // Arrange.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var work = MappingUnitOfWork.Create(harness.Store);
        var children = SqlMapping.Register(work, new MapperChildMapper());
        var parents = SqlMapping.Register(work, new MapperParentMapper());
        var modified = new MapperParent { Id = 1, Name = "before" };
        var restricted = new MapperParent { Id = 2, Name = "restricted parent" };
        var removedChild = new MapperChild { Id = 10, ParentId = 1, Name = "restore this deletion" };
        var survivingChild = new MapperChild { Id = 20, ParentId = 2, Name = "blocks parent deletion" };
        parents.Add(modified);
        parents.Add(restricted);
        children.Add(removedChild);
        children.Add(survivingChild);
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(4);
        parents.Add(new MapperParent { Id = 3, Name = "rollback this insertion" });
        modified.Name = "after";
        children.Remove(removedChild);
        parents.Remove(restricted);

        // Act: ordering executes parent INSERT, parent UPDATE and child DELETE before the
        // final parent DELETE fails RESTRICT because its other child still exists.
        await Should.ThrowAsync<SqlClientException>(async () => await work.SaveChangesAsync(timeout.Token));

        // Assert: none of the three earlier statement kinds published its changes.
        var unchangedParents = await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper())
            .OrderBy(MapperParentMapper.Columns.Id), timeout.Token);
        unchangedParents.Select(parent => parent.Id).ShouldBe(new[] { 1, 2 });
        unchangedParents[0].Name.ShouldBe("before");
        var unchangedChildren = await harness.Store.QueryAsync(SqlMapping.Query(new MapperChildMapper())
            .OrderBy(MapperChildMapper.Columns.Id), timeout.Token);
        unchangedChildren.Select(child => child.Id).ShouldBe(new[] { 10, 20 });
        unchangedChildren[0].Name.ShouldBe("restore this deletion");

        // Explicit correction retains the original four changes and adds the missing child deletion.
        children.Remove(survivingChild);
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(5);
        var savedParents = await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper())
            .OrderBy(MapperParentMapper.Columns.Id), timeout.Token);
        savedParents.Select(parent => parent.Id).ShouldBe(new[] { 1, 3 });
        savedParents[0].Name.ShouldBe("after");
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperChildMapper()), timeout.Token)).ShouldBeEmpty();
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(0);
    }

    /// <summary>Restrict foreign keys require child deletions before their parent regardless of registration order.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Delete: dependent rows delete before their parent")]
    public async Task SaveChangesAsync_DeleteGraph_ShouldReverseDependencyOrder()
    {
        // Arrange.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var work = MappingUnitOfWork.Create(harness.Store);
        var parents = SqlMapping.Register(work, new MapperParentMapper());
        var children = SqlMapping.Register(work, new MapperChildMapper());
        var parent = new MapperParent { Id = 1 };
        var child = new MapperChild { Id = 10, ParentId = 1 };
        parents.Add(parent);
        children.Add(child);
        await work.SaveChangesAsync(timeout.Token);
        parents.Remove(parent);
        children.Remove(child);

        // Act / Assert.
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(2);
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper()), timeout.Token)).ShouldBeEmpty();
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperChildMapper()), timeout.Token)).ShouldBeEmpty();
    }

    /// <summary>A reference can move to a new parent while the old parent is deleted in the same transaction.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Relationship: new parent insert child update and old parent delete share one save")]
    public async Task SaveChangesAsync_MoveRelationship_ShouldOrderInsertUpdateAndDelete()
    {
        // Arrange.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var work = MappingUnitOfWork.Create(harness.Store);
        var children = SqlMapping.Register(work, new MapperChildMapper());
        var parents = SqlMapping.Register(work, new MapperParentMapper());
        var oldParent = new MapperParent { Id = 1, Name = "old" };
        var child = new MapperChild { Id = 10, ParentId = 1 };
        parents.Add(oldParent);
        children.Add(child);
        await work.SaveChangesAsync(timeout.Token);
        parents.Add(new MapperParent { Id = 2, Name = "new" });
        child.ParentId = 2;
        parents.Remove(oldParent);

        // Act / Assert.
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(3);
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper()), timeout.Token))
            .ShouldHaveSingleItem().Id.ShouldBe(2);
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperChildMapper()), timeout.Token))
            .ShouldHaveSingleItem().ParentId.ShouldBe(2);
    }

    /// <summary>Cancellation before save creates no SQL writes and keeps entities available for retry.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Mapping] - Save: pre-cancellation preserves pending changes")]
    public async Task SaveChangesAsync_PreCancelled_ShouldPreservePendingChanges()
    {
        // Arrange.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlMapperTestHarness.StartAsync(timeout.Token);
        var work = MappingUnitOfWork.Create(harness.Store);
        SqlMapping.Register(work, new MapperParentMapper()).Add(new MapperParent { Id = 1 });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        // Act / Assert.
        await Should.ThrowAsync<OperationCanceledException>(async () => await work.SaveChangesAsync(cancelled.Token));
        (await harness.Store.QueryAsync(SqlMapping.Query(new MapperParentMapper()), timeout.Token)).ShouldBeEmpty();
        (await work.SaveChangesAsync(timeout.Token)).ShouldBe(1);
    }
}
