using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlLinearCompositionTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Composition: values, filters, fan-out and boundaries share one binding table")]
    [InlineData("LET x = NULL RETURN x", "null")]
    [InlineData("LET x = 1, y = x + 1 RETURN y", "2")]
    [InlineData("FOR x IN [1] INSERT (:P {k:x * 2}) MATCH (p:P) RETURN p.k", "2")]
    [InlineData("LET x = 1 FILTER UNKNOWN RETURN x", "")]
    [InlineData("LET x = 1 FILTER WHERE x = 1 RETURN x", "1")]
    [InlineData("FOR x IN [1,2] RETURN x", "1,2")]
    [InlineData("UNWIND [1,2] AS x RETURN x", "1,2")]
    [InlineData("FOR x IN [1,2] WITH x RETURN x", "1,2")]
    [InlineData("FOR x IN [1,1.0,2] WITH DISTINCT x RETURN x", "1,2")]
    [InlineData("LET x = 1 RETURN x AS next NEXT RETURN next AS for", "1")]
    [InlineData("LET x = 1 RETURN x + 1 NEXT RETURN 3", "3")]
    [InlineData("FOR x IN NULL RETURN x", "")]
    [InlineData("UNWIND NULL AS x RETURN x", "")]
    [InlineData("LET x = [1,2] FOR y IN x RETURN y", "1,2")]
    [InlineData("LET l=[[1,2]] FOR x IN l FOR y IN x RETURN y", "1,2")]
    [InlineData("LET for=1, next=2 RETURN for + next", "3")]
    [InlineData("RETURN 1 AS next NEXT RETURN next", "1")]
    [InlineData("RETURN 1 + 1 NEXT RETURN 3", "3")]
    [InlineData("FOR x IN [3,1,2] WITH x ORDER BY x OFFSET 1 LIMIT 1 WHERE x > 1 RETURN x", "2")]
    [InlineData("FOR x IN [3,1,2] WITH x ORDER BY x LIMIT 1 WHERE x > 1 RETURN x", "")]
    public async Task Execute_Values_ShouldPreserveLinearSemanticsAsync(string source, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var result = await Rows(session, source);
        // Assert
        string.Join(",", result.Rows.Select(row => row[0] is null ? "null" : Convert.ToString(row[0], System.Globalization.CultureInfo.InvariantCulture))).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composition: scope shrinks and binding kinds are validated per stage")]
    [InlineData("LET x = 1 LET x = 2 RETURN x", "COHDBG003")]
    [InlineData("FOR x IN [1] FOR x IN [2] RETURN x", "COHDBG003")]
    [InlineData("LET x = 1 DELETE x", "COHDBG003")]
    [InlineData("LET x = 1 FILTER x:Foo RETURN x", "COHDBG003")]
    [InlineData("LET x = 1 MATCH (x) RETURN x", "COHDBG003")]
    [InlineData("MATCH (a), (b) WITH a RETURN b", "COHDBG001")]
    [InlineData("MATCH (a), (b) RETURN a NEXT RETURN b", "COHDBG001")]
    [InlineData("MATCH (a) RETURN a.name NEXT RETURN name", "COHDBG001")]
    [InlineData("MATCH (a) WITH a.name RETURN name", "COHDBG001")]
    [InlineData("MATCH (a) DELETE a MATCH (b) RETURN a", "COHDBG003")]
    [InlineData("LET x = 1 WITH x ORDER BY x LET y = missing RETURN y", "COHDBG001")]
    [InlineData("LET x = [1] INSERT (:T {k:x})", "COHDBG003")]
    [InlineData("FOR x IN 1 RETURN x", "COHDBG013")]
    [InlineData("UNWIND 'a' AS x RETURN x", "COHDBG013")]
    public async Task Execute_InvalidScope_ShouldRejectBeforeExecutionAsync(string source, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - FOR: typed lists and null parameters retain elements")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_ForParameters_ShouldExpandListsAndRejectScalarsAsync(bool unwind)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string source = unwind ? "UNWIND $xs AS x RETURN x" : "FOR x IN $xs RETURN x";
        // Act / Assert
        foreach (object? input in new object?[] { new object?[] { 1L, 2L }, new List<long> { 1, 2 }, null })
        {
            var result = await Rows(session, source, new Dictionary<string, object?> { ["xs"] = input });
            result.Rows.Select(row => row[0]).ShouldBe(input is null ? Array.Empty<object?>() : new object?[] { 1L, 2L });
        }
        foreach (object input in new object[] { "text", new byte[] { 1, 2 }, 1L })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source,
                new Dictionary<string, object?> { ["xs"] = input }, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG013");
        }
        var propertyError = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT (:T {k:$xs})",
            new Dictionary<string, object?> { ["xs"] = new List<long> { 1, 2 } }, CancellationToken.None));
        propertyError.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG003");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Composition: insertion labels and own writes are visible to later stages")]
    [InlineData(IsolationLevel.Snapshot, "")]
    [InlineData(IsolationLevel.ReadCommitted, "")]
    [InlineData(IsolationLevel.Snapshot, "NEXT ")]
    [InlineData(IsolationLevel.ReadCommitted, "NEXT ")]
    public async Task Execute_InsertThenMatch_ShouldObserveOwnWritesAsync(IsolationLevel isolation, string next)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(isolation, CancellationToken.None);
        // Act
        var result = await Rows(session, "INSERT (:T {k:1}) " + next + "MATCH (t:T) RETURN t.k");
        // Assert
        result.Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1L });
        await transaction.CommitAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composition: source-ordered mutations evaluate properties from incoming rows")]
    public async Task Execute_Mutations_ShouldUseRowsAndRespectSourceOrderAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var inserted = await session.ExecuteAsync("FOR name IN $names INSERT (:Person {name:name})",
            new Dictionary<string, object?> { ["names"] = new[] { "Alice", "Bob" } }, CancellationToken.None);
        var result = await Rows(session, "MATCH (a:Person) WITH a WHERE a.name = 'Bob' DELETE a MATCH (b:Person) RETURN b.name");
        await session.ExecuteAsync("MATCH (a:Person) DETACH DELETE a INSERT (:Person {name:'Cara'})", cancellationToken: CancellationToken.None);
        // Assert
        inserted.AffectedCount.ShouldBe(2);
        result.Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { "Alice" });
        (await Rows(session, "MATCH (a:Person) RETURN a.name")).Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { "Cara" });
        (await Rows(session, "MATCH (:Missing) INSERT (:Missing) MATCH (n:Missing) RETURN n")).Rows.ShouldBeEmpty();
        (await Rows(session, "INSERT (:T)-[:Fresh]->(:T) MATCH (a:T)-[r:Fresh]->(b:T) RETURN 1")).Rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - WITH: aggregation, alias scope, incoming predicates and carried anchors remain stage-local")]
    public async Task Execute_With_ShouldCarryKindsTypesAndGroupingAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:P {k:1})-[:T]->(:P {k:2}), (:P {k:3})", cancellationToken: CancellationToken.None);
        // Act / Assert
        var count = await Rows(session, "MATCH (a:P) WITH count(a) AS c RETURN c");
        count.Types.ShouldBe(new[] { DatabaseType.Int64 });
        count.Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 3L });
        (await Rows(session, "MATCH (a:P) WITH a.k % 2 AS parity, count(*) AS c RETURN parity, c ORDER BY parity")).Rows.Select(row => row[1]).ShouldBe(new object?[] { 1L, 2L });
        (await Rows(session, "MATCH (a)-[r:T]->(b) WITH a WHERE b.k > 1 RETURN a.k")).Rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await Rows(session, "MATCH (a)-[r:T]->(b) WITH a ORDER BY b.k RETURN a.k")).Rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await Rows(session, "MATCH (a:P) WITH a AS z, a.k AS k MATCH (z)-[:T]->(b) RETURN k, b.k")).Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1L, 2L });
        (await Rows(session, "LET x=1, y=2 WITH y,x RETURN *")).Names.ShouldBe(new[] { "y", "x" });
        await using var tx = await session.BeginTransactionAsync(CancellationToken.None);
        const string anchored = "MATCH (a:P) RETURN a AS z NEXT MATCH (b)-[:T]->(z) RETURN b.k";
        var query = GraphQueryRequest.FromGql(anchored).Statement.GqlExpression;
        var plan = new GraphPlanner(database, tx.Context.Snapshot).Plan(query);
        plan.Stages[3].Plan.MatchClauses.Single().Paths.Single().Anchor.NodeIndex.ShouldBe(1);
        plan.Stages[3].Plan.MatchClauses.Single().Paths.Single().Anchor.Property.ShouldBeNull();
        (await Rows(session, anchored)).Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1L });
        await tx.RollbackAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composition: FOR fan-out shares the 100000 binding cap with MATCH")]
    public async Task Execute_Expansion_ShouldFailAtSharedRowCapAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:P),(:P)", cancellationToken: CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("FOR x IN $xs MATCH (a:P) RETURN x",
            new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 50_001).ToArray() }, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG004");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composition: EXPLAIN maps every stage and never executes writes")]
    public async Task Execute_Explain_ShouldDescribeAllStageKindsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var description = await Rows(session, "EXPLAIN LET x=1 FOR y IN [1] UNWIND [2] AS z FILTER TRUE INSERT (a:P) WITH a DELETE a NEXT MATCH (b) RETURN b");
        // Assert
        description.Rows.Where(row => Equals(row[1], 0)).Select(row => row[2]).ShouldBe(new object?[] { "Let", "For", "Unwind", "Filter", "Insert", "With", "Delete", "Next", "Match", "Return" });
        (await Rows(session, "SHOW LABELS")).Rows.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Composition: hand-built stage requests retain catalog and malformed-stage guards")]
    public async Task Execute_HandBuiltStages_ShouldRejectMalformedShapesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("linear", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        GqlStage[][] malformed = [
            [new GqlInsertStage([])],
            [new GqlInsertStage(null!)],
            [new GqlDeleteStage([])],
            [new GqlDeleteStage(null!)],
            [new GqlReturnStage(null!)],
            [new GqlLetStage(null!), new GqlReturnStage([new GqlProjection(new GqlLiteralExpression(1L))])],
            [new GqlFilterStage(null!), new GqlReturnStage([new GqlProjection(new GqlLiteralExpression(1L))])],
            [new GqlForStage("x", null!), new GqlReturnStage([new GqlProjection(new GqlLiteralExpression(1L))])],
            [new GqlReturnStage([])],
        ];
        // Act / Assert
        foreach (var stages in malformed)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                new GraphQueryRequest(new GqlQueryStatement(new GqlQueryExpression(stages))), CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        }
        var show = new GqlQueryExpression([new GqlLetStage([new GqlProjection(new GqlLiteralExpression(1L), "x")])])
        { CatalogSurface = GqlCatalogSurface.Labels };
        var rejected = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            new GraphQueryRequest(new GqlQueryStatement(show)), CancellationToken.None));
        rejected.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        var catalogWrite = new GqlQueryExpression([new GqlInsertStage([])]) { CatalogSurface = GqlCatalogSurface.Labels };
        var writeRejected = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            new GraphQueryRequest(new GqlQueryStatement(catalogWrite)), CancellationToken.None));
        writeRejected.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0007");
    }

    private static async Task<(string[] Names, DatabaseType[] Types, List<object?[]> Rows)> Rows(GraphDatabaseSession session,
        string source, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await session.ExecuteAsync(source, parameters, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return (result.Columns.Select(column => column.Name).ToArray(), result.Columns.Select(column => column.Type).ToArray(), rows);
    }
}
