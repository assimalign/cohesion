using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphSchemaConstraintTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Constraints: creation checks existing nodes and relationships atomically")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CreateConstraintAsync_ExistingData_ShouldValidateBeforeCommitAsync(bool relationship, bool existence)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, session);
        var id = Guid.NewGuid();
        if (relationship) { await schema.SaveRelationshipTypeAsync(new(id, "T")); }
        else { await schema.SaveLabelAsync(new(id, "N")); }
        string pattern = relationship ? "(a)-[:T {p:'bad'}]->(a)" : "(:N {p:'bad'})";
        await session.ExecuteAsync("INSERT " + pattern);
        var rule = new GraphConstraintMetadata(id, "rule", existence ? "missing" : "p", existence ? null : DatabaseType.Int64);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await schema.CreateConstraintAsync(rule));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03010");
        (await schema.GetConstraintsAsync()).ShouldBeEmpty();
        if (existence) { (await schema.GetPropertyKeysAsync(id)).ShouldNotContain(item => item.Name == "missing"); }
        await session.ExecuteAsync(relationship ? "MATCH ()-[r:T]->() SET r.p=1, r.missing=TRUE" : "MATCH (n:N) SET n.p=1, n.missing=TRUE");
        await schema.CreateConstraintAsync(rule);
        (await schema.FindConstraintAsync("rule")).ShouldBe(rule);
        (await schema.GetConstraintsAsync()).ShouldHaveSingleItem().ShouldBe(rule);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Constraints: CREATE SET REMOVE MERGE and DELETE share enforcement")]
    [InlineData("INSERT (:N)")]
    [InlineData("CREATE (:N {p:'bad'})")]
    [InlineData("MATCH (n:N) SET n.p='bad'")]
    [InlineData("MATCH (n:N) SET n.p=NULL")]
    [InlineData("MATCH (n:N) SET n={}")]
    [InlineData("MATCH (n:N) REMOVE n.p")]
    [InlineData("INSERT (n:Other) SET n:N")]
    [InlineData("MERGE (:N {k:2})")]
    [InlineData("MERGE (n:N {k:1}) ON MATCH SET n.p='bad'")]
    [InlineData("INSERT ()-[:T]->()")]
    [InlineData("MATCH ()-[r:T]->() SET r.p='bad'")]
    [InlineData("MATCH ()-[r:T]->() REMOVE r.p")]
    [InlineData("MERGE (a:N {k:1})-[r:T {k:2}]->(a)")]
    public async Task Execute_ConstraintViolation_ShouldRollbackEveryWritePathAsync(string query)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, session);
        Guid label = Guid.NewGuid(), type = Guid.NewGuid();
        await schema.SaveLabelAsync(new(label, "N"));
        await schema.SaveRelationshipTypeAsync(new(type, "T"));
        foreach (var (id, prefix) in new[] { (label, "node"), (type, "edge") })
        {
            await schema.CreateConstraintAsync(new(id, prefix + "_type", "p", DatabaseType.Int64));
            await schema.CreateConstraintAsync(new(id, prefix + "_exists", "p"));
        }
        await session.ExecuteAsync("INSERT (a:N {k:1,p:1})-[:T {p:1}]->(a)");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH (n:N) RETURN n.p")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH ()-[r:T]->() RETURN r.p")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        await session.ExecuteAsync("MATCH (n:N) DETACH DELETE n");
        (await GraphCatalogIntrospectionTests.Rows(session, "MATCH (n) RETURN n")).ShouldBeEmpty();
        (await schema.GetConstraintsAsync()).Count.ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Constraints: typed creates and stale Snapshot writes cannot bypass a new rule")]
    public async Task CreateNodeAsync_NewConstraintAfterSnapshot_ShouldEnforceOrReportConflictAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var writer = await database.CreateSessionAsync();
        await using var stale = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, writer);
        Guid id = Guid.NewGuid();
        await schema.SaveLabelAsync(new(id, "N"));
        await using var transaction = await stale.BeginTransactionAsync(IsolationLevel.Snapshot);
        await schema.CreateConstraintAsync(new(id, "required", "p"));

        // Act / Assert
        var conflict = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await database.CreateNodeAsync(stale, ["N"]));
        conflict.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG04007");
        await transaction.RollbackAsync();
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await database.CreateNodeAsync(writer, ["N"]));
        refused.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        var node = await database.CreateNodeAsync(writer, ["N"], new Dictionary<string, object?> { ["p"] = 1L });
        await schema.SaveRelationshipTypeAsync(new(Guid.NewGuid(), "T"));
        Guid type = (await schema.GetRelationshipTypesAsync()).ShouldHaveSingleItem().Id;
        await schema.CreateConstraintAsync(new(type, "edge_required", "p"));
        var edgeFailure = await Should.ThrowAsync<DatabaseException>(async () => await database.CreateRelationshipAsync(writer, node.Id, node.Id, "T"));
        edgeFailure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        await database.CreateRelationshipAsync(writer, node.Id, node.Id, "T", new Dictionary<string, object?> { ["p"] = true });
        (await database.DeleteNodeAsync(writer, node.Id)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Constraints: creation validates data committed while waiting for the writer lock")]
    public async Task CreateConstraintAsync_QueuedBehindInvalidData_ShouldRefuseCreationAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var ddl = await database.CreateSessionAsync(timeout.Token);
        var schema = GraphSchema.Open(database, ddl);
        Guid id = Guid.NewGuid();
        await schema.SaveLabelAsync(new(id, "N"), timeout.Token);
        await using var transaction = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await database.CreateNodeAsync(writer, ["N"], cancellationToken: timeout.Token);
        var queued = schema.CreateConstraintAsync(new(id, "required", "p"), timeout.Token).AsTask();
        queued.IsCompleted.ShouldBeFalse();

        // Act
        await transaction.CommitAsync(timeout.Token);
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await queued);

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03010");
        (await schema.GetConstraintsAsync(timeout.Token)).ShouldBeEmpty();
        (await GraphCatalogIntrospectionTests.Rows(ddl, "MATCH (n:N) RETURN n")).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Constraints: named drops preserve other names and independent property rules")]
    public async Task DropConstraintAsync_IndependentRules_ShouldPreserveRemainingEnforcementAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, session);
        Guid label = Guid.NewGuid();
        await schema.SaveLabelAsync(new(label, "N"));
        await schema.CreateConstraintAsync(new(label, "type", "p", DatabaseType.Int64));
        await schema.CreateConstraintAsync(new(label, "exists", "p"));
        await session.ExecuteAsync("INSERT (:N {p:1})");

        // Act / Assert: dropping existence keeps the type rule, and dropping type keeps existence.
        await schema.DropConstraintAsync("exists");
        await session.ExecuteAsync("MATCH (n:N) REMOVE n.p");
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n:N) SET n.p='bad'"));
        await session.ExecuteAsync("MATCH (n:N) SET n.p=2");
        await schema.CreateConstraintAsync(new(label, "exists", "p"));
        await schema.DropConstraintAsync("type");
        await session.ExecuteAsync("MATCH (n:N) SET n.p='valid'");
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n:N) REMOVE n.p"));
        await schema.SavePropertyKeyAsync(new(label, "p", DatabaseType.String, true));
        await schema.DropConstraintAsync("exists");
        (await schema.GetConstraintsAsync()).ShouldBeEmpty();
        (await schema.GetPropertyKeysAsync(label)).ShouldHaveSingleItem().ShouldBe(new(label, "p", DatabaseType.String, true));
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n:N) REMOVE n.p"));
        var missing = await Should.ThrowAsync<DatabaseException>(async () => await schema.DropConstraintAsync("exists"));
        missing.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01021");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Schema names: indexes and node/relationship constraints share one ordinal namespace")]
    public async Task CreateAsync_SharedNames_ShouldRefuseDuplicatesAndReleaseDroppedNamesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("names", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, session);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), t = Guid.NewGuid();
        await schema.SaveLabelAsync(new(a, "A"));
        await schema.SaveLabelAsync(new(b, "B"));
        await schema.SaveRelationshipTypeAsync(new(t, "T"));
        await schema.CreateIndexAsync("A", "shared", "p");

        // Act / Assert
        foreach (Func<ValueTask> duplicate in new Func<ValueTask>[]
        {
            () => schema.CreateIndexAsync("A", "shared", "p"),
            () => schema.CreateIndexAsync("B", "shared", "p"),
            () => schema.CreateConstraintAsync(new(t, "shared", "p")),
        })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await duplicate());
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03008");
        }
        await schema.DropIndexAsync("shared");
        await schema.CreateConstraintAsync(new(t, "shared", "p"));
        await schema.CreateConstraintAsync(new(a, "Shared", "p"));
        var collision = await Should.ThrowAsync<DatabaseException>(async () => await schema.CreateIndexAsync("B", "shared", "q"));
        collision.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03008");
        (await schema.FindIndexAsync("shared")).ShouldBeNull();
        (await schema.FindConstraintAsync("shared")).ShouldNotBeNull().DefinitionId.ShouldBe(t);
        await schema.DropConstraintAsync("shared");
        await schema.CreateIndexAsync("B", "shared", "q");
        (await schema.FindIndexAsync("shared")).ShouldNotBeNull().LabelId.ShouldBe(b);
        (await schema.GetIndexesAsync()).ShouldHaveSingleItem();
        await schema.DropIndexAsync("shared");
        var missing = await Should.ThrowAsync<DatabaseException>(async () => await schema.DropIndexAsync("shared"));
        missing.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01021");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Constraints: equivalent and conflicting rules refuse without weakening metadata")]
    public async Task CreateConstraintAsync_ConflictingRules_ShouldRefuseAndRetainRulesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("constraints", new());
        var database = await engine.CreateDatabaseAsync("graph");
        await using var session = await database.CreateSessionAsync();
        var schema = GraphSchema.Open(database, session);
        Guid id = Guid.NewGuid();
        await schema.SaveLabelAsync(new(id, "N"));
        await schema.CreateConstraintAsync(new(id, "type", "p", DatabaseType.Int64));

        // Act / Assert
        var unsupported = await Should.ThrowAsync<DatabaseException>(async () => await schema.CreateConstraintAsync(new(id, "unsupported", "q", DatabaseType.Guid)));
        unsupported.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        foreach (var type in new[] { DatabaseType.Int64, DatabaseType.String })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await schema.CreateConstraintAsync(new(id, "other", "p", type)));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03009");
        }
        var property = await Should.ThrowAsync<DatabaseException>(async () => await schema.SavePropertyKeyAsync(new(id, "p", DatabaseType.String)));
        property.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03009");
        await session.ExecuteAsync("INSERT (:N), (:N {p:NULL})");
        await schema.SaveLabelAsync(new(id, "N", DatabaseObjectOwner.Schema, "managed"));
        await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await schema.DropConstraintAsync("type"));
        await Should.ThrowAsync<DatabaseObjectLockedException>(async () => await schema.CreateConstraintAsync(new(id, "required", "p")));
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Schema names: a queued creator rechecks global reservations after commit or rollback")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task CreateAsync_QueuedNameCollision_ShouldObserveCommittedReservationAsync(bool rollback, bool firstIndex, bool secondIndex)
    {
        // Arrange: the pending operation and held database lock prove the handoff was exercised.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create("names", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        var one = GraphSchema.Open(database, first);
        var two = GraphSchema.Open(database, second);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        await one.SaveLabelAsync(new(a, "A"), timeout.Token);
        await one.SaveLabelAsync(new(b, "B"), timeout.Token);
        await using var transaction = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        if (firstIndex) { await one.CreateIndexAsync("A", "shared", "p", timeout.Token); }
        else { await one.CreateConstraintAsync(new(a, "shared", "p"), timeout.Token); }
        var queued = (secondIndex ? two.CreateIndexAsync("B", "shared", "p", timeout.Token)
            : two.CreateConstraintAsync(new(b, "shared", "p"), timeout.Token)).AsTask();
        queued.IsCompleted.ShouldBeFalse();

        // Act
        if (rollback) { await transaction.RollbackAsync(timeout.Token); await queued; }
        else
        {
            await transaction.CommitAsync(timeout.Token);
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await queued);
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03008");
        }

        // Assert
        bool expectIndex = rollback ? secondIndex : firstIndex;
        (await two.GetIndexesAsync(timeout.Token)).Count.ShouldBe(expectIndex ? 1 : 0);
        (await two.GetConstraintsAsync(timeout.Token)).Count.ShouldBe(expectIndex ? 0 : 1);
    }
}
