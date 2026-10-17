using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphReturnTests
{
    private const string seed = "INSERT (a:Person {name: 'Alice', age: 42, active: TRUE})-[r:KNOWS]->(b:Person {name: 'Bob', active: FALSE}), (:Person {name: 'Carol'})";

    [Theory(DisplayName = "Cohesion Test [Graph] - RETURN: star preserves declaration order and skips anonymous or repeated bindings")]
    [InlineData("MATCH (a:Person {name: 'Alice'})-[r:KNOWS]->(b) RETURN *", "a,r,b")]
    [InlineData("MATCH p = (a:Person {name: 'Alice'})-[r:KNOWS]->(b) RETURN *", "p,a,r,b")]
    [InlineData("MATCH (a:Person {name: 'Alice'})-[]->(b), (a) RETURN *", "a,b")]
    [InlineData("MATCH (:Person {name: 'Alice'})-[r]->() RETURN *", "r")]
    public async Task Execute_Star_ShouldExpandNamedBindingsInDeclarationOrderAsync(string source, string names)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("return", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);

        // Act
        var result = await RowsAsync(session, source);

        // Assert
        result.Names.ShouldBe(names.Split(','));
        var row = result.Rows.ShouldHaveSingleItem();
        for (int i = 0; i < row.Length; i++)
        {
            switch (result.Names[i])
            {
                case "a": row[i].ShouldBeOfType<GraphNode>().Properties["name"].ShouldBe("Alice"); break;
                case "b": row[i].ShouldBeOfType<GraphNode>().Properties["name"].ShouldBe("Bob"); break;
                case "r": row[i].ShouldBeOfType<GraphRelationship>().Type.ShouldBe("KNOWS"); break;
                case "p": row[i].ShouldBeOfType<GraphPath>().Nodes.Select(node => node.Properties["name"]).ShouldBe(["Alice", "Bob"]); break;
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - RETURN: DISTINCT compares complete tuples and entity or path identity")]
    [InlineData("MATCH (a:Person), (b:Person) RETURN DISTINCT a.active", 3)]
    [InlineData("MATCH (a:Person), (b:Person) RETURN ALL a.active", 9)]
    [InlineData("MATCH (a:Person), (b:Person) RETURN DISTINCT a.active, b.active", 9)]
    [InlineData("MATCH (a:Person), (b:Person) RETURN DISTINCT b", 3)]
    [InlineData("MATCH (c:Person), (a)-[r]->(b) RETURN DISTINCT r", 1)]
    [InlineData("MATCH (c:Person), p = (a)-[r]->(b) RETURN DISTINCT p", 1)]
    [InlineData("MATCH (c:Person), p = (a)-[r]-(b) RETURN DISTINCT p", 2)]
    [InlineData("MATCH p = (a)-[r]->(b) RETURN p, a.name", 1)]
    public async Task Execute_Distinct_ShouldUseProjectedValueIdentityAsync(string source, int count)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("distinct", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);

        // Act
        var result = await RowsAsync(session, source);

        // Assert
        result.Rows.Count.ShouldBe(count);
        if (source.EndsWith("p, a.name", StringComparison.Ordinal))
        {
            result.Rows[0][0].ShouldBeOfType<GraphPath>().Relationships.Count.ShouldBe(1);
            result.Rows[0][1].ShouldBe("Alice");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - RETURN: numeric equality crosses tags without rounding large integers or subnormals")]
    public async Task Execute_DistinctNumerics_ShouldCompareExactValuesAndOrdinalStringsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("numeric-distinct", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        object?[] values = [null, null, (byte)1, (sbyte)1, (short)1, (ushort)1, 1, 1U, 1L, 1UL, 1m, 1f, 1d,
            0, -0d, 9007199254740992L, 9007199254740993L, 9007199254740992d,
            double.Epsilon, 0.1m, 0.1d, decimal.MaxValue, ulong.MaxValue, "A", "a", "A"];
        foreach (var value in values)
        { await session.ExecuteAsync("INSERT (:Value {v: $v})", new Dictionary<string, object?> { ["v"] = value }, CancellationToken.None); }

        // Act
        var result = await RowsAsync(session, "MATCH (n:Value) RETURN DISTINCT n.v");

        // Assert
        result.Rows.Select(row => row[0]).ShouldBe(new object?[]
        { null, (byte)1, 0, 9007199254740992L, 9007199254740993L, double.Epsilon, 0.1m, 0.1d, decimal.MaxValue, ulong.MaxValue, "A", "a" });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - RETURN: DISTINCT follows every mutation and empty matches retain column metadata")]
    public async Task Execute_DistinctMutation_ShouldWriteForEveryInputBindingAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("mutation-return", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);

        // Act
        var projected = await RowsAsync(session, "MATCH (a:Person) INSERT (:Output {v: 1}) RETURN DISTINCT 1 AS one");
        var inserted = await RowsAsync(session, "MATCH (n:Output) RETURN n.v");
        var empty = await RowsAsync(session, "MATCH (n:Missing) RETURN DISTINCT 1 + 1 AS two");

        // Assert
        projected.Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1L });
        inserted.Rows.Count.ShouldBe(3);
        empty.Rows.ShouldBeEmpty();
        empty.Names.ShouldBe(["two"]);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - RETURN: entity equality uses binding identity across independent materializations")]
    public async Task Execute_EntityComparison_ShouldCompareIdentitiesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("identity-return", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);

        // Act
        var result = await RowsAsync(session, "MATCH (a:Person), (b:Person) WHERE a = b RETURN a.name, a = b AS same");

        // Assert
        result.Rows.Count.ShouldBe(3);
        result.Rows.Select(row => row[0]).ShouldBe(new object?[] { "Alice", "Bob", "Carol" });
        result.Rows.ShouldAllBe(row => Equals(row[1], true));
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - RETURN: named parameters retain their scalar runtime type")]
    [InlineData("MATCH (a:Person) RETURN $p AS p", 3)]
    [InlineData("RETURN @p AS p", 1)]
    public async Task Execute_ParameterProjection_ShouldKeepRuntimeTypeAsync(string source, int count)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("parameter-return", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);

        // Act
        var result = await RowsAsync(session, source, new Dictionary<string, object?> { ["p"] = 5 });

        // Assert
        result.Names.ShouldBe(["p"]);
        result.Rows.Count.ShouldBe(count);
        foreach (var row in result.Rows) { row[0].ShouldBeOfType<int>().ShouldBe(5); }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - RETURN: binding failures remain coded before data access")]
    [InlineData("RETURN missing", "COHDBG001")]
    [InlineData("RETURN missing.age + 1", "COHDBG001")]
    [InlineData("MATCH (a) RETURN b.age + 1", "COHDBG001")]
    [InlineData("MATCH (a) RETURN $missing", "COHDBG001")]
    [InlineData("RETURN $missing", "COHDBG001")]
    [InlineData("EXPLAIN RETURN $missing", "COHDBG001")]
    [InlineData("RETURN FALSE AND $missing", "COHDBG001")]
    [InlineData("MATCH p = (a)-[r]->(b) RETURN p.name", "COHDBG003")]
    public async Task Execute_InvalidProjection_ShouldKeepDiagnosticAsync(string source, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("bad-return", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Paths: DISTINCT removes duplicate bindings and one-column star is accepted")]
    [InlineData("MATCH (a:Person), (b:Person) RETURN DISTINCT b", 3)]
    [InlineData("MATCH (a:Person) RETURN *", 3)]
    [InlineData("MATCH (b:Person), p = ()-[]->() RETURN DISTINCT p", 1)]
    [InlineData("MATCH (b:Person), ()-[r]->() RETURN DISTINCT r", 1)]
    public async Task Execute_PathsDistinct_ShouldProjectEntitiesByIdentityAsync(string source, int count)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("path-return", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(source), CancellationToken.None);

        // Assert
        result.ShouldBeOfType<GraphPathsQueryResult>().Paths.Count.ShouldBe(count);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - RETURN: duplicate final column names fail planning even without input rows")]
    [InlineData("RETURN 1 AS x, 2 AS x")]
    [InlineData("MATCH (a) RETURN a, a")]
    [InlineData("MATCH (a) RETURN a.x, a.x")]
    [InlineData("MATCH (a) RETURN a.x, 1 AS `a.x`")]
    [InlineData("RETURN 1, 2 AS `1`")]
    [InlineData("RETURN 1 + 2, 3 AS `1 + 2`")]
    [InlineData("EXPLAIN RETURN 1 AS x, 2 AS x")]
    public async Task Execute_DuplicateColumnNames_ShouldRejectBeforeExecutionAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("duplicate-return", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBG015");
        diagnostic.Start.ShouldNotBeNull();
        error.Message.ShouldContain("is declared more than once", Case.Sensitive);
        error.Message.ShouldNotContain("COHDBG015", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - RETURN: duplicate names prevent mutation and names compare ordinally")]
    public async Task Execute_DuplicateMutationColumns_ShouldRejectAndKeepSessionUsableAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("duplicate-mutation", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("INSERT (:Unwritten) RETURN 1 AS x, 2 AS x", cancellationToken: CancellationToken.None));
        var result = await RowsAsync(session, "RETURN 1 AS x, 2 AS X");

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG015");
        (await RowsAsync(session, "SHOW LABELS")).Rows.ShouldBeEmpty();
        result.Names.ShouldBe(["x", "X"]);
        result.Rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1L, 2L });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Paths: computed, multiple and multi-binding star projections are rejected")]
    [InlineData("MATCH (a) RETURN a.age + 1")]
    [InlineData("MATCH (a) RETURN a, a")]
    [InlineData("MATCH (a) RETURN a AS x, a AS x")]
    [InlineData("MATCH (a) RETURN a.x, a.x")]
    [InlineData("INSERT (:Unwritten) RETURN 1 AS x, 2 AS x")]
    [InlineData("MATCH (a)-[r]->(b) RETURN *")]
    [InlineData("MATCH (a) RETURN a IS NULL")]
    [InlineData("RETURN 1")]
    public async Task Execute_InvalidPathProjection_ShouldRejectCompleteRequestAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("path-return", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(source), CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        error.Message.ShouldContain("exactly one path or bound entity projection", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - RETURN: source-free AST projections retain public construction and EXPLAIN maps their plans")]
    public async Task Execute_HandBuiltExpression_ShouldComputeAndNameColumnAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("ast-return", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var expression = new GqlBinaryExpression(new GqlLiteralExpression(1L), "+", new GqlLiteralExpression(1L));
        var query = new GqlQueryExpression([], null, [], [], false, [new GqlProjection(expression)]);

        // Act
        await using var result = (await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var description = await RowsAsync(session, "EXPLAIN RETURN DISTINCT 1 + 1 AS two");

        // Assert
        result.Columns.ShouldHaveSingleItem().Name.ShouldBe("(1 + 1)");
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(2L); }
        description.Rows.Count(row => Equals(row[2], "Distinct")).ShouldBe(1);
        description.Rows.Count(row => Equals(row[2], "Project") && Equals(row[3], "two")).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - RETURN: source-free computed names preserve a complete label expression")]
    public async Task Execute_LongLabelProjection_ShouldKeepCompleteColumnNameAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("label-return", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        var labels = new GqlLabelConjunction(Enumerable.Repeat<GqlLabelExpression>(new GqlLabelName("Person"), 100).ToArray());
        var match = GraphQueryRequest.FromGql("MATCH (a:Person) RETURN a").Statement.GqlExpression.Matches;
        var query = new GqlQueryExpression(match, null, [], [], false, [new GqlProjection(new GqlLabeledPredicate("a", labels))]);

        // Act
        await using var result = (await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); }

        // Assert
        result.Columns.ShouldHaveSingleItem().Name.ShouldBe("a IS LABELED " + labels);
        result.Columns[0].Name.Length.ShouldBeGreaterThan(256);
        values.ShouldBe(new object?[] { true, true, true });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - RETURN: new AST flags cannot silently compose with SHOW")]
    [InlineData(true, GqlSetQuantifier.All)]
    [InlineData(false, GqlSetQuantifier.Distinct)]
    public async Task Execute_CatalogWithReturnFlags_ShouldRejectCompositionAsync(bool star, GqlSetQuantifier quantifier)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("catalog-return", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var query = new GqlQueryExpression([], null, [], [], false, [])
        { CatalogSurface = GqlCatalogSurface.Labels, ReturnStar = star, SetQuantifier = quantifier };

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        error.Message.ShouldBe("SHOW cannot be composed with graph clauses.");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - RETURN: a hand-built DISTINCT without items cannot silently mutate")]
    public async Task Execute_DistinctWithoutItems_ShouldRejectBeforeMutationAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("invalid-return", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var insertion = GraphQueryRequest.FromGql("INSERT (:Unwritten)").Statement.GqlExpression;
        var query = new GqlQueryExpression([], null, insertion.Creates, [], false, [])
        { SetQuantifier = GqlSetQuantifier.Distinct };

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        (await RowsAsync(session, "SHOW LABELS")).Rows.ShouldBeEmpty();
    }

    private static async Task<(string[] Names, List<object?[]> Rows)> RowsAsync(GraphDatabaseSession session,
        string source, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await session.ExecuteAsync(source, parameters, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return (result.Columns.Select(column => column.Name).ToArray(), rows);
    }
}
