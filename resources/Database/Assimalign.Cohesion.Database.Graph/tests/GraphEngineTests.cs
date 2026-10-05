using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphEngineTests
{
    [Fact]
    public async Task Cyclic_traversal_visits_each_node_once_excludes_start_and_honors_depth_and_direction()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var db = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        var a = await db.CreateNodeAsync(session, ["Person"]);
        var b = await db.CreateNodeAsync(session, ["Person"]);
        var c = await db.CreateNodeAsync(session, ["Person"]);
        await db.CreateRelationshipAsync(session, a.Id, b.Id, "LINK");
        await db.CreateRelationshipAsync(session, b.Id, c.Id, "LINK");
        await db.CreateRelationshipAsync(session, c.Id, a.Id, "LINK");
        await db.CreateRelationshipAsync(session, a.Id, a.Id, "LINK");
        (await Visit(new(a.Id, MaxDepth: int.MaxValue))).ShouldBe([b.Id, c.Id]);
        (await Visit(new(a.Id, MaxDepth: 1))).ShouldBe([b.Id]);
        (await Visit(new(a.Id, GraphDirection.Incoming))).ShouldBe([c.Id]);
        (await Visit(new(a.Id, RelationshipType: "MISSING", MaxDepth: 100))).ShouldBeEmpty();
        (await Visit(new(a.Id, MaxDepth: 0))).ShouldBeEmpty();
        async Task<List<GraphNodeId>> Visit(GraphTraversal traversal)
        {
            var ids = new List<GraphNodeId>();
            await foreach (var node in db.TraverseAsync(session, traversal)) { ids.Add(node.Id); }
            return ids;
        }
    }

    [Fact]
    public async Task Typed_delete_cascades_and_rollback_restores_node_relationships_and_index()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var db = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        var a = await db.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["name"] = "a" });
        var b = await db.CreateNodeAsync(session, ["Person"]);
        await db.CreateRelationshipAsync(session, a.Id, b.Id, "LINK");
        await GraphSchema.Open(db, session).CreateIndexAsync("Person", "by_name", "name");
        await using var tx = await session.BeginTransactionAsync();
        (await db.DeleteNodeAsync(session, a.Id)).ShouldBeTrue();
        (await db.GetNodeAsync(session, a.Id)).ShouldBeNull();
        await tx.RollbackAsync();
        (await db.GetNodeAsync(session, a.Id)).ShouldNotBeNull();
        var visited = new List<GraphNode>();
        await foreach (var node in db.TraverseAsync(session, new(a.Id))) { visited.Add(node); }
        visited.ShouldHaveSingleItem().Id.ShouldBe(b.Id);
    }

    [Theory]
    [InlineData(IsolationLevel.Snapshot, false)]
    [InlineData(IsolationLevel.ReadCommitted, true)]
    public async Task Transactions_enforce_the_requested_visibility(IsolationLevel isolation, bool seesNew)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var db = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var reader = await db.CreateSessionAsync();
        await using var writer = await db.CreateSessionAsync();
        await using var tx = await reader.BeginTransactionAsync(isolation);
        var node = await db.CreateNodeAsync(writer, ["Person"]);
        ((await db.GetNodeAsync(reader, node.Id)) is not null).ShouldBe(seesNew);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Definitions_are_discoverable_and_schema_owned_changes_name_object_schema_and_operation()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var db = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        var schema = GraphSchema.Open(db, session);
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Person", DatabaseObjectOwner.Schema, "PeopleSchema");
        var type = new GraphRelationshipTypeMetadata(Guid.NewGuid(), "KNOWS", DatabaseObjectOwner.Schema, "PeopleSchema");
        await schema.SaveLabelAsync(label);
        await schema.SaveRelationshipTypeAsync(type);
        (await schema.GetLabelsAsync()).ShouldHaveSingleItem().ShouldBe(label);
        (await schema.GetRelationshipTypesAsync()).ShouldHaveSingleItem().ShouldBe(type);
        var error = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await schema.DropLabelAsync(label.Name));
        error.Message.ShouldContain("Person"); error.Message.ShouldContain("PeopleSchema"); error.Message.ShouldContain("DROP LABEL");
        await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await schema.SaveLabelAsync(label));
        error = await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await schema.DropRelationshipTypeAsync(type.Name));
        error.Message.ShouldContain("KNOWS"); error.Message.ShouldContain("PeopleSchema"); error.Message.ShouldContain("DROP RELATIONSHIP TYPE");
        await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await schema.SaveRelationshipTypeAsync(type));
        await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await schema.CreateIndexAsync(label.Name, "by_name", "name"));
    }

    [Fact]
    public async Task Required_property_and_type_mismatch_reject_the_complete_write()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var db = (IGraphDatabase)await engine.CreateDatabaseAsync("graph");
        await using var session = await db.CreateSessionAsync();
        var schema = GraphSchema.Open(db, session);
        var label = new GraphLabelMetadata(Guid.NewGuid(), "Person");
        await schema.SaveLabelAsync(label);
        await schema.SavePropertyKeyAsync(new(label.Id, "age", DatabaseType.Int64, true));
        var error = await Should.ThrowAsync<DatabaseException>(async () => await db.CreateNodeAsync(session, ["Person"]));
        error.Message.ShouldContain("COHDBG003");
        await Should.ThrowAsync<DatabaseException>(async () => await db.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["age"] = "old" }));
        (await db.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["age"] = 42L })).Properties["age"].ShouldBe(42L);
    }

    [Fact]
    public async Task File_lifecycle_enumeration_workers_reopen_drop_and_idempotent_disposal()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-graph-" + Guid.NewGuid().ToString("N"));
        try
        {
            GraphNodeId id;
            await using (var engine = GraphDatabaseEngine.Create(new() { RootPath = root }))
            {
                engine.Workers.Select(worker => worker.Kind).Distinct().Count().ShouldBe(4);
                engine.State.ShouldBe(EngineState.Running);
                var db = (IGraphDatabase)await engine.CreateDatabaseAsync("persisted");
                await using var session = await db.CreateSessionAsync();
                id = (await db.CreateNodeAsync(session, ["Person"])).Id;
                engine.TryGetDatabase("PERSISTED", out var found).ShouldBeTrue(); found.ShouldBeSameAs(db);
                await Should.ThrowAsync<DatabaseException>(async () => await engine.CreateDatabaseAsync("persisted"));
            }
            var reopened = GraphDatabaseEngine.Create(new() { RootPath = root });
            var names = new List<string>();
            await foreach (var db in reopened.GetDatabasesAsync()) { names.Add(db.Name.ToString()); }
            names.ShouldBe(["persisted"]);
            var restored = (IGraphDatabase)await reopened.OpenDatabaseAsync("persisted");
            await using (var session = await restored.CreateSessionAsync()) { (await restored.GetNodeAsync(session, id)).ShouldNotBeNull(); }
            await reopened.DropDatabaseAsync("persisted");
            reopened.TryGetDatabase("persisted", out _).ShouldBeFalse();
            await Should.ThrowAsync<DatabaseNotFoundException>(async () => await reopened.OpenDatabaseAsync("persisted"));
            await reopened.DisposeAsync(); reopened.Dispose(); reopened.State.ShouldBe(EngineState.Disposed);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Format: a database whose indexes are in B-tree page format 1 is refused at open with COHDBI001, its files untouched (#1194)")]
    public async Task Open_IndexPagesInFormatOne_ShouldBeRefused()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-graph-format-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Arrange: a closed database with a property index, its index pages rewritten
            // into the layout engines before #1194 wrote (entries ordered by key alone).
            await using (var engine = GraphDatabaseEngine.Create(new() { RootPath = root }))
            {
                var db = (IGraphDatabase)await engine.CreateDatabaseAsync("legacy");
                await using var session = await db.CreateSessionAsync();
                await db.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["name"] = "a" });
                await GraphSchema.Open(db, session).CreateIndexAsync("Person", "by_name", "name");
            }

            LegacyBTreePages.DowngradeDataFiles(root).ShouldBeGreaterThan(0);
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

            // Act
            await using var reopened = GraphDatabaseEngine.Create(new() { RootPath = root });
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("legacy"));

            // Assert: the coded refusal, with the index manager's as its cause; the
            // check ran before recovery, so the database is left as it was.
            failure.Message.ShouldStartWith("Database 'legacy' cannot be opened. " + IndexFormatException.ErrorCode + ": ", Case.Sensitive);
            failure.Message.ShouldContain("uses B-tree page format 1, but this engine supports only format 2", Case.Sensitive);
            failure.InnerException.ShouldBeOfType<IndexFormatException>().FoundVersion.ShouldBe(1);
            reopened.TryGetDatabase("legacy", out _).ShouldBeFalse();
            await reopened.DisposeAsync();

            foreach (var (path, bytes) in before)
            {
                File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes).ShouldBeTrue($"{path} was modified by the refused open");
            }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Format: a database in storage format 2 (the format before #1253) is refused at open with COHDBS001 naming it, its files untouched (#1251, #1253)")]
    public async Task Open_StorageFormatTwo_ShouldBeRefusedNamingTheDatabase()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-graph-storage-format-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Arrange: a closed database whose page 0 names storage format 2, the format before #1253.
            await using (var engine = GraphDatabaseEngine.Create(new() { RootPath = root }))
            {
                var db = (IGraphDatabase)await engine.CreateDatabaseAsync("legacy");
                await using var session = await db.CreateSessionAsync();
                await db.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["name"] = "a" });
            }

            StorageFormatFiles.WriteVersion(Path.Combine(root, "legacy", "graph.dat"), version: 2);
            var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

            // Act
            await using var reopened = GraphDatabaseEngine.Create(new() { RootPath = root });
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("legacy"));

            // Assert: the storage's coded refusal, named for the database; nothing written.
            failure.Message.ShouldStartWith("Database 'legacy' cannot be opened. " + StorageFormatException.ErrorCode + ": ", Case.Sensitive);
            failure.Message.ShouldContain("uses storage format 2, but this engine supports only storage format 3", Case.Sensitive);
            failure.InnerException.ShouldBeOfType<StorageFormatException>().FoundVersion.ShouldBe(2);
            reopened.TryGetDatabase("legacy", out _).ShouldBeFalse();
            await reopened.DisposeAsync();

            Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length.ShouldBe(before.Count);
            foreach (var (path, bytes) in before)
            {
                File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes).ShouldBeTrue($"{path} was modified by the refused open");
            }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }
}
