using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlUnknownPropertyWarningTests
{
    private const string seed = "INSERT (:Keep {name:'k'})-[:LINK {edgeKey:1}]->(:Other {otherKey:2})";

    [Theory(DisplayName = "Cohesion Test [Graph] - Unknown entity properties warn before executing every read position (#1249)")]
    [InlineData("MATCH (n:Keep {never_seen:1}) RETURN n.name", 0)]
    [InlineData("MATCH ()-[r:LINK {never_seen:1}]->() RETURN r.edgeKey", 0)]
    [InlineData("MATCH (n:Keep) RETURN n.never_seen", 1)]
    [InlineData("MATCH ()-[r:LINK]->() RETURN r.never_seen", 1)]
    [InlineData("MATCH (n:Keep) WHERE n.never_seen IS NULL RETURN n.name", 1)]
    [InlineData("MATCH (n:Keep) RETURN n.name ORDER BY n.never_seen", 1)]
    [InlineData("MATCH (n:Keep) RETURN n AS a ORDER BY a.never_seen", 1)]
    [InlineData("MATCH (n:Keep)-[r:LINK]->() RETURN CASE WHEN TRUE THEN n ELSE r END AS entity ORDER BY entity.never_seen", 1)]
    [InlineData("MATCH (n:Keep) WITH n AS a RETURN a.never_seen", 1)]
    [InlineData("MATCH (n:Keep) RETURN [x IN [n] | x.never_seen]", 1)]
    [InlineData("MATCH (n:Keep) RETURN EXISTS { (n)-[:LINK]->(b {never_seen:1}) }", 1)]
    [InlineData("MATCH ()-[r:LINK]->() RETURN startNode(r).never_seen", 1)]
    public async Task ExecuteAsync_UnknownEntityProperty_ShouldWarnAndReturnNormalRowsAsync(string source, int count)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("properties", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        var transaction = await session.BeginTransactionAsync(CancellationToken.None);

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, cancellationToken: CancellationToken.None);
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; }

        // Assert
        rows.ShouldBe(count);
        var warning = result.Diagnostics.ShouldHaveSingleItem();
        warning.Code.ShouldBe(GraphTokenResolver.UnknownPropertyCode);
        warning.Severity.ShouldBe(DiagnosticSeverity.Warning);
        warning.Message.ShouldNotBeNull().ShouldContain("'never_seen'", Case.Sensitive);
        warning.Message.ShouldNotContain("CG01023", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.Active);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Known-anywhere names, maps, dynamic access and writes have no property warning (#1249)")]
    [InlineData("MATCH (n:Keep) RETURN n.otherKey, n.edgeKey")]
    [InlineData("MATCH ()-[r:LINK]->() RETURN r.name")]
    [InlineData("RETURN {name:1}.never_seen")]
    [InlineData("LET n={name:1} RETURN n.never_seen")]
    [InlineData("MATCH (n:Keep) RETURN properties(n).never_seen")]
    [InlineData("MATCH (n:Keep) RETURN n['never_seen']")]
    [InlineData("MATCH (n:Keep) RETURN n[$key]")]
    [InlineData("MATCH (n:Keep) RETURN [n IN [{name:1}] | n.never_seen]")]
    [InlineData("MATCH (n:Keep) WHERE n.never_seen IS NULL SET n.introduced=1 RETURN n.never_seen")]
    [InlineData("MATCH (n:Keep {never_seen:1}) DELETE n")]
    [InlineData("INSERT (:New {never_seen:1})")]
    public async Task ExecuteAsync_ExcludedAccess_ShouldNotWarnAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("properties", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync(source, new Dictionary<string, object?> { ["key"] = "never_seen" }, CancellationToken.None);
        if (result is DatabaseResultSet rows) { await rows.DisposeAsync(); }

        // Assert
        result.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - One property warning per ordinal name across the complete statement (#1249)")]
    [InlineData("MATCH (n:Keep) WHERE n.never_seen IS NULL RETURN n.never_seen AS x ORDER BY n.never_seen", 1)]
    [InlineData("MATCH (n:Keep) RETURN n.never_seen AS x UNION ALL MATCH (n:Other) RETURN n.never_seen AS x", 2)]
    [InlineData("MATCH (n:Keep) RETURN n.never_seen AS x NEXT MATCH (n:Other) RETURN n.never_seen AS x", 1)]
    [InlineData("MATCH (n:Keep) RETURN EXISTS { (n)-[:LINK]->(b) WHERE b.never_seen IS NULL }, n.never_seen", 1)]
    [InlineData("MATCH (n:Keep {never_seen:1})-[r:LINK {never_seen:1}]->() RETURN n.never_seen,r.never_seen", 0)]
    public async Task ExecuteAsync_RepeatedUnknownName_ShouldDeduplicateAsync(string source, int count)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("properties", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, cancellationToken: CancellationToken.None);
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; }

        // Assert
        rows.ShouldBe(count);
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01023");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Typed AST requests keep located property diagnostics on base and typed results (#1249)")]
    public async Task ExecuteAsync_TypedRequest_ShouldRetainDiagnosticCarrierAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("properties", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        const string source = "MATCH (n:Keep) RETURN n.never_seen";
        var parsed = GraphQueryRequest.FromGql(source);
        var direct = new GraphQueryRequest(new GqlQueryStatement(new GqlQueryExpression(
            [new GqlPathPattern([new GqlNodePattern("n", ["Keep"], new Dictionary<string, GqlExpression>())], [])],
            null, [], [], false, [new GqlProjection(new GqlPropertyExpression("n", "never_seen"))])));

        // Act
        await using var located = (DatabaseResultSet)await session.ExecuteAsync(parsed, CancellationToken.None);
        await using var unlocated = (DatabaseResultSet)await session.ExecuteAsync(direct, CancellationToken.None);

        // Assert
        var warning = located.Diagnostics.ShouldHaveSingleItem();
        warning.Location.ShouldBe(DiagnosticLocation.Absolute);
        source[warning.Start!.Value..warning.End!.Value].ShouldBe("n.never_seen");
        ((DatabaseResult)located).Diagnostics.ShouldBeSameAs(located.Diagnostics);
        unlocated.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01023");
        unlocated.Diagnostics[0].Start.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Property tokens obey snapshots, rollback and retained discovered metadata (#1249)")]
    public async Task ExecuteAsync_PropertyDiscovery_ShouldRespectTransactionVisibilityAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("properties", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var observer = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await session.ExecuteAsync("MATCH (n:Keep) SET n.later=1", cancellationToken: CancellationToken.None);

        // Act / Assert
        await using (var own = (DatabaseResultSet)await session.ExecuteAsync("MATCH (n:Keep) RETURN n.later", cancellationToken: CancellationToken.None))
        { own.Diagnostics.ShouldBeEmpty(); }
        await using (var other = (DatabaseResultSet)await observer.ExecuteAsync("MATCH (n:Keep) RETURN n.later", cancellationToken: CancellationToken.None))
        { other.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01023"); }
        await transaction.RollbackAsync(CancellationToken.None);
        await using (var rolledBack = (DatabaseResultSet)await observer.ExecuteAsync("MATCH (n:Keep) RETURN n.later", cancellationToken: CancellationToken.None))
        { rolledBack.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01023"); }
        await session.ExecuteAsync("MATCH (n:Keep) SET n.later=1 REMOVE n.later", cancellationToken: CancellationToken.None);
        await using var retained = (DatabaseResultSet)await observer.ExecuteAsync("MATCH (n:Keep) RETURN n.later", cancellationToken: CancellationToken.None);
        retained.Diagnostics.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Undoing property discovery removes the token for the still-active writer (#1249)")]
    public async Task ExecuteAsync_UndonePropertyDiscovery_ShouldWarnAgainAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("properties", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        var marker = await UndoMarkerDriver.CreateAsync(database.Coordinator, transaction.Context);
        await session.ExecuteAsync("MATCH (n:Keep) SET n.undone=1", cancellationToken: CancellationToken.None);

        // Act
        await UndoMarkerDriver.UndoAsync(database.Coordinator, transaction.Context, marker);
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("MATCH (n:Keep) RETURN n.undone", cancellationToken: CancellationToken.None);

        // Assert
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01023");
        transaction.State.ShouldBe(TransactionState.Active);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Known property probes allocate nothing and read no metadata records (#1249)")]
    public async Task CheckProperty_KnownName_ShouldAvoidAllocationAndRecordReadsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("properties", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        await database.RunAsync(session, operation =>
        {
            var resolver = new GraphTokenResolver(database.Catalog, operation.Context.Snapshot);
            for (int i = 0; i < 100; i++) { resolver.CheckProperty("name"); }
            int reads = 0;
            database.Catalog.BeforeRecordRead = () => reads++;
            try
            {
                // Act
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1_000; i++) { resolver.CheckProperty("name"); }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                // Assert
                allocated.ShouldBe(0);
                reads.ShouldBe(0);
                resolver.Warnings.ShouldBeEmpty();
            }
            finally { database.Catalog.BeforeRecordRead = null; }
            return ValueTask.FromResult(0);
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Declared property tokens survive index drop and reopening without entity values (#1249)")]
    public async Task ExecuteAsync_DeclaredPropertyAfterReopen_ShouldRemainKnownAsync()
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-property-warning", Guid.NewGuid().ToString("N"));
        var options = new GraphDatabaseEngineOptions { RootPath = FileSystemPath.Parse(directory) };
        try
        {
            await using (var engine = GraphDatabaseEngine.Create("properties", options))
            {
                var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                var schema = GraphSchema.Open(database, session);
                await schema.CreateIndexAsync("Fresh", "fresh_index", "declared", CancellationToken.None);
                await schema.DropIndexAsync("fresh_index", CancellationToken.None);
            }

            // Act
            await using var reopened = GraphDatabaseEngine.Create("properties", options);
            var opened = await reopened.OpenDatabaseAsync("graph", CancellationToken.None);
            await using var observer = await opened.CreateSessionAsync(CancellationToken.None);
            await using var result = (DatabaseResultSet)await observer.ExecuteAsync(
                "MATCH (n:Fresh) RETURN n.declared", cancellationToken: CancellationToken.None);

            // Assert
            result.Diagnostics.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Distinct property names retain ordinal identity despite diagnostic shortening (#1249)")]
    public async Task ExecuteAsync_DistinctLongNames_ShouldWarnForEveryNameAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("properties", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string prefix = new('p', 300);
        string source = $"MATCH (n) RETURN n.{prefix}a AS a,n.{prefix}b AS b,n.Mixed AS c,n.mixed AS d,n.Mixed AS e";

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, cancellationToken: CancellationToken.None);

        // Assert
        result.Diagnostics.Count.ShouldBe(4);
        result.Diagnostics.ShouldAllBe(item => item.Code == "CG01023" && item.Severity == DiagnosticSeverity.Warning);
        result.Diagnostics[0].Message.ShouldBe(result.Diagnostics[1].Message);
    }
}
