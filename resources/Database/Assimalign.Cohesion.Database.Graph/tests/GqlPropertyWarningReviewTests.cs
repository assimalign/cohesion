using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlPropertyWarningReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Stored property discovery is independent of definition metadata, including reopen (#1249)")]
    [InlineData("INSERT ({present:42})", "MATCH (n) RETURN n.present", false)]
    [InlineData("INSERT ({present:42})", "MATCH (n) RETURN n.present", true)]
    [InlineData("INSERT (:Managed {present:42})", "MATCH (n:Managed) RETURN n.present", false)]
    [InlineData("INSERT (:Managed {present:42})", "MATCH (n:Managed) RETURN n.present", true)]
    [InlineData("INSERT ()-[:Managed {present:42}]->()", "MATCH ()-[r:Managed]->() RETURN r.present", false)]
    [InlineData("INSERT ()-[:Managed {present:42}]->()", "MATCH ()-[r:Managed]->() RETURN r.present", true)]
    [InlineData("MERGE (n {present:42})", "MATCH (n) RETURN n.present", false)]
    [InlineData("MERGE (n {present:42})", "MATCH (n) RETURN n.present", true)]
    [InlineData("MERGE ()-[r:Managed {present:42}]->()", "MATCH ()-[r:Managed]->() RETURN r.present", false)]
    [InlineData("MERGE ()-[r:Managed {present:42}]->()", "MATCH ()-[r:Managed]->() RETURN r.present", true)]
    [InlineData("INSERT (n) SET n.present=42", "MATCH (n) RETURN n.present", false)]
    [InlineData("INSERT (n) SET n.present=42", "MATCH (n) RETURN n.present", true)]
    [InlineData("INSERT (n:Managed) SET n.present=42", "MATCH (n:Managed) RETURN n.present", false)]
    [InlineData("INSERT (n:Managed) SET n.present=42", "MATCH (n:Managed) RETURN n.present", true)]
    [InlineData("INSERT ()-[r:Managed]->() SET r.present=42", "MATCH ()-[r:Managed]->() RETURN r.present", false)]
    [InlineData("INSERT ()-[r:Managed]->() SET r.present=42", "MATCH ()-[r:Managed]->() RETURN r.present", true)]
    [InlineData("INSERT (n {present:42}) REMOVE n.present", "MATCH (n) RETURN n.present", false)]
    [InlineData("INSERT (n {present:42}) REMOVE n.present", "MATCH (n) RETURN n.present", true)]
    [InlineData("INSERT (n:Managed {present:42}) REMOVE n.present", "MATCH (n:Managed) RETURN n.present", false)]
    [InlineData("INSERT (n:Managed {present:42}) REMOVE n.present", "MATCH (n:Managed) RETURN n.present", true)]
    [InlineData("INSERT ()-[r:Managed {present:42}]->() REMOVE r.present", "MATCH ()-[r:Managed]->() RETURN r.present", false)]
    [InlineData("INSERT ()-[r:Managed {present:42}]->() REMOVE r.present", "MATCH ()-[r:Managed]->() RETURN r.present", true)]
    public async Task ExecuteAsync_StoredProperty_ShouldRemainKnownAfterReopenAsync(string write, string read, bool reopen)
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-property-review", Guid.NewGuid().ToString("N"));
        var options = new GraphDatabaseEngineOptions { RootPath = FileSystemPath.Parse(directory) };
        try
        {
            await using (var engine = GraphDatabaseEngine.Create("review", options))
            {
                var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                var schema = GraphSchema.Open(database, session);
                await schema.SaveLabelAsync(new(Guid.NewGuid(), "Managed", DatabaseObjectOwner.Schema, "schema"), CancellationToken.None);
                await schema.SaveRelationshipTypeAsync(new(Guid.NewGuid(), "Managed", DatabaseObjectOwner.Schema, "schema"), CancellationToken.None);

                // Act / Assert
                await session.ExecuteAsync(write, cancellationToken: CancellationToken.None);
                if (!reopen) { await AssertReadAsync(session, read, write.Contains("REMOVE", StringComparison.Ordinal) ? null : 42L); }
            }
            await using var reopened = GraphDatabaseEngine.Create("review", options);
            var opened = await reopened.OpenDatabaseAsync("graph", CancellationToken.None);
            await using var observer = await opened.CreateSessionAsync(CancellationToken.None);
            if (reopen) { await AssertReadAsync(observer, read, write.Contains("REMOVE", StringComparison.Ordinal) ? null : 42L); }
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - ORDER BY member accesses resolve the projected alias in text and direct ASTs (#1249)")]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ExecuteAsync_ShadowedOrderMember_ShouldUseProjectedReceiverAsync(bool typed, bool entity, bool parenthesized)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("review", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Keep {a:1})", cancellationToken: CancellationToken.None);
        string source = (entity ? "LET n={a:1} MATCH (m:Keep) RETURN m AS n" : "MATCH (n:Keep) RETURN {a:1} AS n") +
            (parenthesized ? " ORDER BY (n).never_seen" : " ORDER BY n.never_seen");
        var parsed = GraphQueryRequest.FromGql(source);
        var query = parsed.Statement.GqlExpression;
        var stages = query.Stages.ToArray();
        var resultStage = (GqlReturnStage)stages[^1];
        stages[^1] = new GqlReturnStage(resultStage.Projections)
        { OrderBy = [new GqlSortItem(new GqlMemberExpression(new GqlVariableExpression("n"), "never_seen"))] };
        var request = typed ? new GraphQueryRequest(new GqlQueryStatement(new GqlQueryExpression(stages))) : parsed;

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; }

        // Assert
        rows.ShouldBe(1);
        if (entity) { result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01023"); }
        else { result.Diagnostics.ShouldBeEmpty(); }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Writing statements perform no nested property-warning analysis or catalog probe (#1249)")]
    [InlineData("MATCH (n:Keep) WHERE EXISTS { (n)-[:LINK]->(b) WHERE b.never_seen IS NULL } SET n.updated=1 RETURN n.a")]
    [InlineData("MATCH (n:Keep) SET n.updated=size([(n)-[:LINK]->(b) WHERE b.never_seen IS NULL | b]) RETURN n.a")]
    public async Task Plan_WritingCorrelatedRead_ShouldSkipPropertyAnalysisAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("review", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Keep {a:1})-[:LINK]->()", cancellationToken: CancellationToken.None);
        int checks = 0, probes = 0;
        database.Catalog.BeforePropertyCheck = () => checks++;
        database.Catalog.BeforePropertyKeyLookup = _ => probes++;
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, cancellationToken: CancellationToken.None);

            // Assert
            result.Diagnostics.ShouldBeEmpty();
            checks.ShouldBe(0);
            probes.ShouldBe(0);
        }
        finally { database.Catalog.BeforePropertyCheck = null; database.Catalog.BeforePropertyKeyLookup = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Resolve each property name once across repeated accesses and subqueries (#1249)")]
    public async Task Plan_RepeatedProperties_ShouldResolveEachDistinctNameOnceAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("review", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Keep {a:1,b:2})-[:LINK]->()", cancellationToken: CancellationToken.None);
        var lookups = new List<string>();
        database.Catalog.BeforePropertyKeyLookup = lookups.Add;
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(
                "MATCH (n:Keep {a:1}) WHERE n.a=1 RETURN n.a,n.b,n.a AS again,n.never_seen,EXISTS { (n)-[:LINK]->(b) WHERE b.never_seen IS NULL } ORDER BY n.b,n.never_seen",
                cancellationToken: CancellationToken.None);

            // Assert
            lookups.Order(StringComparer.Ordinal).ShouldBe(new[] { "a", "b", "never_seen" });
            result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01023");
        }
        finally { database.Catalog.BeforePropertyKeyLookup = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Definition-independent discovery obeys undo and snapshots and refreshes a reused request (#1249)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PrivateDiscovery_ShouldRespectUndoAndSnapshotsAsync(bool managed)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("review", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await using var observer = await database.CreateSessionAsync(CancellationToken.None);
        Guid owner = Guid.NewGuid();
        var schema = GraphSchema.Open(database, writer);
        if (managed) { await schema.SaveLabelAsync(new(owner, "Managed", DatabaseObjectOwner.Schema, "schema"), CancellationToken.None); }
        await writer.ExecuteAsync(managed ? "INSERT (:Managed)" : "INSERT ()", cancellationToken: CancellationToken.None);
        var request = GraphQueryRequest.FromGql("MATCH (n) RETURN n.present");
        await using var old = await observer.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await using var transaction = await writer.BeginTransactionAsync(CancellationToken.None);
        var marker = await UndoMarkerDriver.CreateAsync(database.Coordinator, transaction.Context);

        // Act / Assert: own writes, partial undo, then another writer's commit at an old snapshot.
        await writer.ExecuteAsync("MATCH (n) SET n.present=42", cancellationToken: CancellationToken.None);
        await AssertReadAsync(writer, "MATCH (n) RETURN n.present", 42L);
        await UndoMarkerDriver.UndoAsync(database.Coordinator, transaction.Context, marker);
        await using (var undone = (DatabaseResultSet)await writer.ExecuteAsync(request, CancellationToken.None))
        { undone.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01023"); }
        await writer.ExecuteAsync("MATCH (n) SET n.present=42", cancellationToken: CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
        await using (var stale = (DatabaseResultSet)await observer.ExecuteAsync(request, CancellationToken.None))
        {
            stale.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01023");
            await foreach (var row in stale.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBeNull(); }
        }
        await old.RollbackAsync(CancellationToken.None);
        await using (var fresh = (DatabaseResultSet)await observer.ExecuteAsync(request, CancellationToken.None))
        {
            fresh.Diagnostics.ShouldBeEmpty();
            await foreach (var row in fresh.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(42L); }
        }
        if (managed) { (await schema.GetPropertyKeysAsync(owner, CancellationToken.None)).ShouldBeEmpty(); }
    }

    private static async Task AssertReadAsync(GraphDatabaseSession session, string source, object? expected)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, cancellationToken: CancellationToken.None);
        result.Diagnostics.ShouldBeEmpty();
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(expected); count++; }
        count.ShouldBe(1);
    }
}
