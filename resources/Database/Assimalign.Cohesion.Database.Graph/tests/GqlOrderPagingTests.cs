using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Language;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlOrderPagingTests
{
    private const string seed = "INSERT (:Person {name:'Alice', age:42, active:TRUE}), (:Person {name:'Bob', age:17, active:FALSE}), (:Person {name:'Isolated', age:30})";
    private readonly ITestOutputHelper _output;

    public GqlOrderPagingTests(ITestOutputHelper output) => _output = output;

    [Theory(DisplayName = "Cohesion Test [Graph] - Ordering and paging: row sequences follow keys, null placement and aliases")]
    [InlineData("RETURN a.name ORDER BY a.age", "Bob,Isolated,Alice")]
    [InlineData("RETURN a.name ORDER BY a.age DESC", "Alice,Isolated,Bob")]
    [InlineData("RETURN a.name ORDER BY a.age ASCENDING", "Bob,Isolated,Alice")]
    [InlineData("RETURN a.name ORDER BY a.age DESCENDING", "Alice,Isolated,Bob")]
    [InlineData("RETURN a.name ORDER BY a.active", "Bob,Alice,Isolated")]
    [InlineData("RETURN a.name ORDER BY a.active DESC", "Isolated,Alice,Bob")]
    [InlineData("RETURN a.name ORDER BY a.active ASC NULLS FIRST", "Isolated,Bob,Alice")]
    [InlineData("RETURN a.name ORDER BY a.active DESC NULLS LAST", "Alice,Bob,Isolated")]
    [InlineData("RETURN a.name ORDER BY a.absent NULLS FIRST, a.age DESC", "Alice,Isolated,Bob")]
    [InlineData("RETURN a.name ORDER BY a.age + 1", "Bob,Isolated,Alice")]
    [InlineData("RETURN a.name AS person ORDER BY person DESC", "Isolated,Bob,Alice")]
    [InlineData("RETURN a.name ORDER BY CASE WHEN a.age > 20 THEN 1 ELSE 0 END, a.name DESC", "Bob,Isolated,Alice")]
    [InlineData("RETURN a.name ORDER BY COALESCE(a.active, TRUE), a.name", "Bob,Alice,Isolated")]
    [InlineData("RETURN a.name ORDER BY a.age DESC SKIP 1 LIMIT 1", "Isolated")]
    [InlineData("RETURN a.name OFFSET 5", "")]
    [InlineData("RETURN a.name LIMIT 0", "")]
    [InlineData("RETURN a.name ORDER BY a.name OFFSET 1 LIMIT 2", "Bob,Isolated")]
    [InlineData("RETURN a.name OFFSET 9223372036854775807 LIMIT 9223372036854775807", "")]
    [InlineData("RETURN DISTINCT a.name ORDER BY a.name", "Alice,Bob,Isolated")]
    [InlineData("RETURN DISTINCT a.name AS person ORDER BY person", "Alice,Bob,Isolated")]
    [InlineData("RETURN DISTINCT a.name ORDER BY a.name + ''", "Alice,Bob,Isolated")]
    [InlineData("RETURN a.name, {age:a.age} AS person ORDER BY person.age", "Bob,Isolated,Alice")]
    [InlineData("RETURN a.name ORDER BY CASE a.age WHEN 42 THEN a WHEN 17 THEN [] ELSE {} END", "Isolated,Alice,Bob")]
    [InlineData("RETURN a.name, a.age / 100 AS bucket ORDER BY a.age / 100.0", "Bob,Isolated,Alice")]
    [InlineData("RETURN a.name, a.age / 100.0 AS bucket ORDER BY a.age / 100", "Alice,Bob,Isolated")]
    [InlineData("RETURN a.name, a AS p ORDER BY p:Person, p.name", "Alice,Bob,Isolated")]
    [InlineData("RETURN a.name, COALESCE(a, a) AS x ORDER BY x:Person, x.name", "Alice,Bob,Isolated")]
    public async Task Execute_OrderPagingTail_ShouldReturnExpectedSequenceAsync(string tail, string names)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("order", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "MATCH (a:Person) " + tail);
        // Assert
        rows.Select(row => row[0]).ShouldBe(names.Length == 0 ? [] : names.Split(','));
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Ordering and paging: both sigils and paging spellings bind before execution")]
    [InlineData("OFFSET @start LIMIT $rows")]
    [InlineData("SKIP $start LIMIT @rows")]
    public async Task Execute_Parameters_ShouldPageSortedRowsAsync(string tail)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("page-parameters", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "MATCH (a:Person) RETURN a.name ORDER BY a.name " + tail,
            new Dictionary<string, object?> { ["$rows"] = 2, ["@start"] = 1L });
        // Assert
        rows.Select(row => row[0]).ShouldBe(["Bob", "Isolated"]);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Ordering and paging: invalid paging parameters reject before mutation even for empty matches")]
    [InlineData(-1)]
    [InlineData(1.5)]
    [InlineData(1.0)]
    [InlineData(null)]
    [InlineData("2")]
    [InlineData(true)]
    public async Task Execute_InvalidPageValue_ShouldRejectBeforeMutationAsync(object? value)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("bad-page", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["page"] = value };
        // Act / Assert
        foreach (string source in new[] { "INSERT (:Unwritten) RETURN 1 LIMIT $page", "MATCH (n:Missing) RETURN n LIMIT @page", "EXPLAIN RETURN 1 SKIP $page" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source, parameters, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
        }
        (await RowsAsync(session, "SHOW LABELS")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Paging: non-finite and unsupported CLR inputs keep the binding diagnostic")]
    public async Task Execute_InvalidClrPageValue_ShouldReportBindingCodeBeforeMutationAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("bad-clr-page", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        object[] values = [double.NaN, double.PositiveInfinity, float.NegativeInfinity, '2', new object()];
        // Act / Assert
        foreach (object value in values)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "INSERT (:Unwritten) RETURN 1 LIMIT $page", new Dictionary<string, object?> { ["page"] = value }, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        }
        (await RowsAsync(session, "SHOW LABELS")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Ordering and paging: DISTINCT rejects hidden bindings and missing parameters stay coded")]
    [InlineData("MATCH (a:Person) RETURN DISTINCT a.name ORDER BY a.age")]
    [InlineData("MATCH (a:Person) RETURN DISTINCT a.name ORDER BY a.age + 1")]
    [InlineData("MATCH (a:Person) RETURN DISTINCT a.age / 100 AS bucket ORDER BY a.age / 100.0")]
    [InlineData("MATCH (p:Person) RETURN DISTINCT p.age / 2 AS h ORDER BY p.age / 2.0")]
    [InlineData("MATCH (a) RETURN a.name LIMIT $missing")]
    [InlineData("EXPLAIN RETURN 1 OFFSET @missing")]
    [InlineData("MATCH (a) RETURN a.name ORDER BY $missing")]
    public async Task Execute_InvalidSortBinding_ShouldRejectDuringPlanningAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("bad-sort", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering and paging: DISTINCT computed expressions compare structurally and project before paging")]
    public async Task Execute_DistinctComputedKey_ShouldSortProjectedTuplesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("distinct-sort", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "MATCH (a:Person), (b:Person) RETURN DISTINCT a.age+1 AS age ORDER BY (a.age + 1) DESC OFFSET 1 LIMIT 2");
        var entities = await RowsAsync(session, "MATCH (a:Person) RETURN DISTINCT a ORDER BY a.age");
        // Assert
        rows.Select(row => row[0]).ShouldBe(new object?[] { 31L, 18L });
        entities.Select(row => ((GraphNode)row[0]!).Properties["name"]).ShouldBe(["Bob", "Isolated", "Alice"]);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Ordering: DISTINCT permits a projected entity through its original binding")]
    [InlineData("a.name", "Alice,Bob,Isolated")]
    [InlineData("a:Person, a.name", "Alice,Bob,Isolated")]
    public async Task Execute_RenamedDistinctEntity_ShouldKeepBindingInScopeAsync(string key, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("renamed-order", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "MATCH (a:Person) RETURN DISTINCT a AS person ORDER BY " + key);
        // Assert
        rows.Select(row => ((GraphNode)row[0]!).Properties["name"]).ShouldBe(expected.Split(','));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering: integer and floating literals remain distinct projection shapes")]
    public async Task Execute_TypedLiteralKey_ShouldEvaluateWrittenArithmeticAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("typed-order", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:P {v:3}), (:P {v:2})", cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "MATCH (p:P) RETURN p.v AS v, p.v / 2 AS h ORDER BY p.v / 2.0");
        // Assert
        rows.Select(row => row[0]).ShouldBe(new object?[] { 2L, 3L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering: computed entity label tests resolve at runtime")]
    public async Task Execute_ComputedEntityLabel_ShouldAcceptEntitiesAndRejectScalarValuesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("entity-label-order", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("MATCH (a:Person {name:'Bob'}) INSERT (a)-[:MANAGES]->(:Manager {name:'Zoe'})", cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "MATCH (a:Person) OPTIONAL MATCH (a)-[:MANAGES]->(m) RETURN a.name, COALESCE(m, a) AS who ORDER BY who:Manager DESC, a.name");
        var scalar = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "MATCH (a:Person) RETURN CASE WHEN a.age > 20 THEN a ELSE 1 END AS who ORDER BY who:Person", cancellationToken: CancellationToken.None));
        var staticScalar = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "MATCH (a:Person) RETURN a.name AS n ORDER BY n:Person", cancellationToken: CancellationToken.None));
        await using var nodeAlias = (await session.ExecuteAsync(
            "MATCH (a:Person) RETURN a AS person ORDER BY person:Person", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        await using var relationshipAlias = (await session.ExecuteAsync(
            "MATCH ()-[r:MANAGES]->() RETURN r AS relAlias ORDER BY relAlias:MANAGES", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        // Assert
        rows.Select(row => row[0]).ShouldBe(["Bob", "Alice", "Isolated"]);
        scalar.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        staticScalar.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        nodeAlias.Diagnostics.ShouldBeEmpty();
        relationshipAlias.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Ordering: stable ties preserve reverse input order beyond insertion sort")]
    [InlineData("ASC")]
    [InlineData("DESC")]
    public async Task Execute_EqualKeys_ShouldKeepReverseInputOrderAsync(string direction)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("stable-order", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var input = Enumerable.Range(0, 64).Reverse().ToArray();
        await session.ExecuteAsync("INSERT " + string.Join(",", input.Select(i => $"(:N {{v:{i}, k:1}})")), cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "MATCH (a:N) RETURN a.v ORDER BY a.k " + direction);
        // Assert
        rows.Select(row => row[0]).ShouldBe(input.Select(i => (object?)(long)i));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering: entities, relationships, lists and paths follow identity and path sequence")]
    public async Task Execute_EntityPathKeys_ShouldFollowRecordedOrderAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("path-order", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (s:S {name:'S'})-[:R {v:2}]->(b:B {name:'B'})-[:R {v:3}]->(c:C {name:'C'}), (s)-[:R {v:1}]->(c)", cancellationToken: CancellationToken.None);
        // Act
        var nodes = await RowsAsync(session, "MATCH (a) RETURN a.name ORDER BY a DESC");
        var relationships = await RowsAsync(session, "MATCH ()-[r]->() RETURN r.v ORDER BY r DESC");
        var paths = await RowsAsync(session, "MATCH p=(s:S)-[r]->(b) RETURN b.name ORDER BY p");
        var mixed = await RowsAsync(session, "MATCH p=(s:S)-[r]->(b) RETURN b.name ORDER BY CASE b.name WHEN 'B' THEN p ELSE [] END");
        var relationshipAlias = await RowsAsync(session, "MATCH ()-[r]->() RETURN r.v, r AS relAlias ORDER BY relAlias:R, relAlias.v");
        // Assert
        nodes.Select(row => row[0]).ShouldBe(["C", "B", "S"]);
        relationships.Select(row => row[0]).ShouldBe(new object?[] { 1L, 3L, 2L });
        paths.Select(row => row[0]).ShouldBe(["B", "C"]);
        mixed.Select(row => row[0]).ShouldBe(["C", "B"]);
        relationshipAlias.Select(row => row[0]).ShouldBe(new object?[] { 1L, 2L, 3L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering and paging: mixed and nested values use total order and exact numbers")]
    public async Task Execute_CompositeAndNumericKeys_ShouldUseRecordedTotalOrderAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("mixed-order", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        var cases = new (object? A, object? B, object? C, string Names)[]
        {
            (1, "x", false, "Bob,Isolated,Alice"),
            (new object?[] { 1 }, new Dictionary<string, object?> { ["a"] = 1 }, "x", "Bob,Alice,Isolated"),
            (new object?[] { null }, new object?[] { 1 }, Array.Empty<object?>(), "Isolated,Bob,Alice"),
            (new object?[] { 1, null }, new object?[] { 1, false }, new object?[] { 1 }, "Isolated,Bob,Alice"),
            (new Dictionary<string, object?> { ["a"] = null }, new Dictionary<string, object?> { ["a"] = 1 }, new Dictionary<string, object?>(), "Isolated,Bob,Alice"),
            (new Dictionary<string, object?> { ["z"] = 0 }, new Dictionary<string, object?> { ["a"] = 9 }, new Dictionary<string, object?> { ["b"] = 1 }, "Bob,Isolated,Alice"),
            (9007199254740993L, 9007199254740992d, 9007199254740992L, "Bob,Isolated,Alice"),
            (0.1d, 0.1m, double.Epsilon, "Isolated,Bob,Alice"),
            ("a", "A", "b", "Bob,Alice,Isolated"),
        };
        // Act / Assert
        foreach (var test in cases)
        {
            var rows = await RowsAsync(session, "MATCH (a:Person) RETURN a.name ORDER BY CASE a.age WHEN 42 THEN $a WHEN 17 THEN $b ELSE $c END",
                new Dictionary<string, object?> { ["a"] = test.A, ["b"] = test.B, ["c"] = test.C });
            rows.Select(row => row[0]).ShouldBe(test.Names.Split(','));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering and paging: mutation visits all bindings before LIMIT")]
    public async Task Execute_LimitedMutation_ShouldWriteForEveryBindingAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("mutation-page", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "MATCH (a:Person) INSERT (a)-[:TAGGED]->(:Tag) RETURN a.name ORDER BY a.name LIMIT 1");
        var tags = await RowsAsync(session, "MATCH (:Person)-[:TAGGED]->(t:Tag) RETURN t");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { "Alice" });
        tags.Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering and paging: optional null extension uses explicit null placement")]
    public async Task Execute_OptionalKey_ShouldSortNullExtendedValuesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-order", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "MATCH (a:Person) OPTIONAL MATCH (b:Person {name:'Bob'}) WHERE a.name = b.name RETURN a.name ORDER BY b.age NULLS FIRST, a.name");
        // Assert
        rows.Select(row => row[0]).ShouldBe(["Alice", "Isolated", "Bob"]);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering and paging: LIMIT stops an oversized match before retaining its fan-out (#1533, D4)")]
    public async Task Execute_LimitOnOversizedMatch_ShouldStopUpstreamAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = GraphDatabaseEngine.Create("page-cap", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        // 317 squared exceeds the cap with only 317 stored nodes.
        await session.ExecuteAsync("INSERT " + string.Join(",", Enumerable.Repeat("(:N)", 317)), cancellationToken: timeout.Token);
        var watch = Stopwatch.StartNew();
        // Act
        var rows = await RowsAsync(session, "MATCH (a:N), (b:N) RETURN a LIMIT 1");
        // Assert
        rows.ShouldHaveSingleItem();
        session.LastStatementMetrics!.Physical.ShouldBe(2);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        _output.WriteLine($"LIMIT 1 over a 100,489-match fan-out: {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering and paging: ten thousand rows sort before LIMIT ten")]
    public async Task Execute_TenThousandRows_ShouldSortBeforeLimitAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = GraphDatabaseEngine.Create("order-measure", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT " + string.Join(",", Enumerable.Range(0, 100).Select(i => "(:N {v:" + i + "})")), cancellationToken: timeout.Token);
        var watch = Stopwatch.StartNew();
        // Act
        var rows = await RowsAsync(session, "MATCH (a:N), (b:N) RETURN a.v * 100 + b.v AS v ORDER BY v DESC LIMIT 10");
        // Assert
        rows.Select(row => row[0]).ShouldBe(Enumerable.Range(9990, 10).Reverse().Select(value => (object?)(long)value));
        _output.WriteLine($"10,000 rows ORDER BY LIMIT 10: {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering and paging: hand-built ASTs cannot silently bypass shape checks")]
    public async Task Execute_InvalidAstTail_ShouldKeepCodedGuardsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("bad-sort-ast", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var projection = new GqlProjection(new GqlLiteralExpression(1L), "one");
        var queries = new GqlQueryExpression[]
        {
            new([], null, [], [], false, [projection]) { OrderBy = null! },
            new([], null, [], [], false, [projection]) { OrderBy = [null!] },
            new([], null, [], [], false, [projection]) { OrderBy = [new(new GqlLiteralExpression(1L), (GqlSortDirection)99)] },
            new([], null, [], [], false, [projection]) { OrderBy = [new(new GqlLiteralExpression(1L), NullOrdering: (GqlNullOrdering)99)] },
            new([], null, [], [], false, [projection]) { OrderBy = [new(new GqlLogicalExpression(GqlLogicalOperator.And, null!))] },
            new([], null, [], [], false, [projection]) { OrderBy = [new(new GqlLogicalExpression(GqlLogicalOperator.And, [new GqlLiteralExpression(true), null!]))] },
            new([], null, [], [], false, [projection]) { Limit = new GqlBinaryExpression(new GqlLiteralExpression(1), "+", new GqlLiteralExpression(1)) },
            new([], null, [], [], false, []) { CatalogSurface = GqlCatalogSurface.Labels, Limit = new GqlLiteralExpression(1L) },
        };
        // Act / Assert
        foreach (var query in queries)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering and paging: a path alias remains invalid as a property owner on empty matches")]
    public async Task Execute_PathAliasProperty_ShouldRejectBeforeReadsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("bad-sort-path", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("MATCH p = (a)->(b) RETURN p AS route ORDER BY route.name", cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
    }

    private static async Task<List<object?[]>> RowsAsync(GraphDatabaseSession session, string source, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await session.ExecuteAsync(source, parameters, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
