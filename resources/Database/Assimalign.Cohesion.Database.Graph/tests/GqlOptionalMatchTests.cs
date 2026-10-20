using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Transactions;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlOptionalMatchTests
{
    private const string seed = "INSERT (a:Person {name:'Alice', age:42})-[:KNOWS]->(b:Person {name:'Bob', age:17}), (:Person {name:'Isolated', age:30})";

    [Theory(DisplayName = "Cohesion Test [Graph] - OPTIONAL MATCH: unknown names null-extend and warn without emptying the statement")]
    [InlineData("MATCH (a:Person) OPTIONAL MATCH (b:Missing) RETURN a.name, b.name", "COHDBG010", false)]
    [InlineData("MATCH (a:Person) OPTIONAL MATCH (a)-[:MISSING]->(b) RETURN a.name, b.name", "COHDBG011", false)]
    [InlineData("MATCH (a:Person) OPTIONAL MATCH (b) WHERE b:Missing RETURN a.name, b.name", "COHDBG010", false)]
    [InlineData("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) MATCH (c:Missing) RETURN a.name, b.name", "COHDBG010", true)]
    public async Task Execute_UnknownOptionalName_ShouldKeepNullRowsAndTransactionAsync(string source, string code, bool empty)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-warning", new());
        var database = await engine.CreateDatabaseAsync("optional", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        // Act
        var plan = await PlanAsync(database, session, source);
        await using var result = (await session.ExecuteAsync(source, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await RowsAsync(result);
        // Assert
        plan.MatchesNothing.ShouldBe(empty);
        result.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        transaction.State.ShouldBe(TransactionState.Active);
        rows.Select(row => row[0]).ShouldBe(empty ? [] : ["Alice", "Bob", "Isolated"]);
        rows.ShouldAllBe(row => row[1] == null);
        await transaction.CommitAsync(CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - OPTIONAL MATCH: an empty leading optional supplies explicit null entity and path bindings")]
    public async Task Execute_EmptyLeadingOptional_ShouldBindEveryVariableToNullAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-empty", new());
        var database = await engine.CreateDatabaseAsync("optional", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await QueryAsync(session, "OPTIONAL MATCH p = (a)-[r]->(b) RETURN a, r, b, p, a.name, a IS NULL, a:Person");
        var dropped = await QueryAsync(session, "MATCH (a) OPTIONAL MATCH (b) RETURN b.name");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { null, null, null, null, null, true, null });
        dropped.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - OPTIONAL MATCH: null insert endpoints fail with a coded error and roll back every write")]
    [InlineData("OPTIONAL MATCH (a:Person {name:'Nobody'}) INSERT (a)-[:KNOWS]->(:Person)")]
    [InlineData("OPTIONAL MATCH (a:Person {name:'Nobody'}) INSERT (:Person)-[:KNOWS]->(a)")]
    public async Task Execute_NullInsertionEndpoint_ShouldRollBackAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-insert", new());
        var database = await engine.CreateDatabaseAsync("optional", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHDBG003");
        (await QueryAsync(session, "MATCH (n) RETURN n.name")).Count.ShouldBe(3);
        (await QueryAsync(session, "MATCH ()-[r]->() RETURN r")).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - OPTIONAL MATCH: deletion ignores null nodes and relationships and preserves an explicit transaction")]
    public async Task Execute_NullDelete_ShouldCountOnlyExistingEntitiesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-delete", new());
        var database = await engine.CreateDatabaseAsync("optional", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        // Act
        var result = await session.ExecuteAsync("MATCH (a:Person) OPTIONAL MATCH (a)-[r:KNOWS]->(b) DELETE r", cancellationToken: CancellationToken.None);
        var nullNode = await session.ExecuteAsync("OPTIONAL MATCH (n:Person {name:'Nobody'}) DELETE n", cancellationToken: CancellationToken.None);
        // Assert
        result.AffectedCount.ShouldBe(1);
        nullNode.AffectedCount.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.Active);
        await transaction.CommitAsync(CancellationToken.None);
        (await QueryAsync(session, "MATCH (n) RETURN n.name")).Count.ShouldBe(3);
        (await QueryAsync(session, "MATCH ()-[r]->() RETURN r")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Paths: optional-only node, edge and path projections fail while planning even when matched")]
    [InlineData("OPTIONAL MATCH (a:Person) RETURN a")]
    [InlineData("MATCH (a:Person {name:'Alice'}) OPTIONAL MATCH (a)-[r:KNOWS]->(b) RETURN b")]
    [InlineData("MATCH (a:Person {name:'Alice'}) OPTIONAL MATCH (a)-[r:KNOWS]->(b) RETURN r")]
    [InlineData("MATCH (a:Person {name:'Alice'}) OPTIONAL MATCH p = (a)-[r:KNOWS]->(b) RETURN p")]
    [InlineData("OPTIONAL MATCH (n:Person {name:'Nobody'}) RETURN n")]
    public async Task Plan_OptionalOnlyPathProjection_ShouldRejectAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-paths", new());
        var database = await engine.CreateDatabaseAsync("optional", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act / Assert
        var planError = await Should.ThrowAsync<DatabaseException>(async () => await PlanAsync(database, session, source, paths: true));
        planError.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(source), CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Paths: a mandatory binding remains projectable alongside optionals")]
    [InlineData("MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) RETURN a", 3)]
    [InlineData("OPTIONAL MATCH (a:Person) MATCH (a) RETURN a", 3)]
    public async Task Execute_MandatoryPathBinding_ShouldReturnPathsAsync(string source, int count)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-path-bound", new());
        var database = await engine.CreateDatabaseAsync("optional", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        var result = (await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(source), CancellationToken.None)).ShouldBeOfType<GraphPathsQueryResult>();
        // Assert
        result.Paths.Count.ShouldBe(count);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MATCH: predicates cannot reference later declarations")]
    [InlineData("MATCH (a) WHERE b.age > 1 MATCH (b) RETURN a.name")]
    [InlineData("MATCH (a) OPTIONAL MATCH (a)->(b) WHERE c.age > 1 MATCH (c) RETURN a.name")]
    [InlineData("OPTIONAL MATCH (a) WHERE b:Person MATCH (b) RETURN a.name")]
    public async Task Plan_ForwardPredicateReference_ShouldRejectAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-scope", new());
        var database = await engine.CreateDatabaseAsync("optional", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - MATCH: malformed clause ASTs and optional SHOW composition fail with COHDBG001")]
    public async Task Execute_MalformedClauseAst_ShouldRejectAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-ast", new());
        var database = await engine.CreateDatabaseAsync("optional", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        GqlPathPattern path = new([new GqlNodePattern("a", [], new Dictionary<string, GqlExpression>())], []);
        GqlMatchClause[] invalid = [null!, new(null!, null, true), new([], null, true), new([null!], null, true)];
        var queries = invalid.Select(clause => new GqlQueryExpression([clause], [], [], false, [])).ToList();
        queries.Add(new GqlQueryExpression(null!, [], [], false, []));
        queries.Add(new GqlQueryExpression([new GqlMatchClause([path], null, true)], [], [], false, [new GqlProjection(new GqlVariableExpression("missing"))]));
        queries.Add(new GqlQueryExpression([new GqlMatchClause([path], new GqlPropertyExpression("later", "age"), true)], [], [], false, []));
        queries.Add(new GqlQueryExpression([new GqlMatchClause([path], null, true)], [], [], false, []) { CatalogSurface = GqlCatalogSurface.Labels });
        // Act / Assert
        foreach (var query in queries)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MATCH: null-bound variables consume no scan or expansion candidates")]
    [InlineData("MATCH (a)->(b)", "a")]
    [InlineData("MATCH (a)->(b)", "b")]
    [InlineData("MATCH (a)-[r]->(b)", "r")]
    public async Task MatchAsync_NullBinding_ShouldConsumeNoCandidatesAsync(string pattern, string variable)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-null-anchor", new());
        var database = await engine.CreateDatabaseAsync("optional", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        var budget = new GraphPlanExecutor.ExpansionBudget();
        var output = new List<Dictionary<string, object?>>();
        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(pattern + " RETURN a").Statement.GqlExpression);
            await GraphPlanExecutor.MatchAsync(database, operation, plan.MatchClauses[0].Paths[0],
                new Dictionary<string, object?> { [variable] = null }, output, budget, null, CancellationToken.None);
            return true;
        }, CancellationToken.None);
        // Assert
        output.ShouldBeEmpty();
        budget.Consumed.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MATCH: materialization and candidate budgets remain effective across clauses")]
    [InlineData(317, "MATCH (a:N) MATCH (b:N) RETURN a", "100000 path matches")]
    [InlineData(317, "MATCH (a:N) OPTIONAL MATCH (b:N) RETURN a", "100000 path matches")]
    [InlineData(1001, "MATCH (a:N) OPTIONAL MATCH (b:N) WHERE FALSE RETURN a", "1000000 candidate expansions")]
    public async Task Execute_AcrossClauseBounds_ShouldKeepLimitsAsync(int nodes, string source, string message)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = GraphDatabaseEngine.Create("optional-bounds", new());
        var database = await engine.CreateDatabaseAsync("optional", cancellation.Token);
        await using var session = await database.CreateSessionAsync(cancellation.Token);
        await session.ExecuteAsync("INSERT " + string.Join(",", Enumerable.Repeat("(:N)", nodes)), cancellationToken: cancellation.Token);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source, cancellationToken: cancellation.Token));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG004");
        error.Message.ShouldContain(message, Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - OPTIONAL MATCH: numeric equality, ordering and IN retain exact mixed numeric comparisons")]
    [InlineData("$m = $d")]
    [InlineData("$m IN [$d]")]
    [InlineData("$m >= $d")]
    public async Task Execute_OptionalNumericPredicate_ShouldCompareExactlyAsync(string predicate)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-numbers", new());
        var database = await engine.CreateDatabaseAsync("optional", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["m"] = 0.1m, ["d"] = 0.1d };
        // Act
        await using var result = (await session.ExecuteAsync("OPTIONAL MATCH (a:Person) WHERE " + predicate + " RETURN DISTINCT a.name", parameters, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        // Assert
        (await RowsAsync(result)).ShouldHaveSingleItem().ShouldBe(new object?[] { null });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MATCH: earlier node bindings precede scans in either pattern spelling")]
    [InlineData("OPTIONAL MATCH (b)<-(a)", true)]
    [InlineData("OPTIONAL MATCH (b:N)->(a)", true)]
    [InlineData("OPTIONAL MATCH (b)->(a)->(c)", true)]
    [InlineData("MATCH (b)<-(a)", false)]
    [InlineData("MATCH (b:N)->(a)", false)]
    [InlineData(", (b)<-(a)", false)]
    [InlineData(", (b:N)->(a)", false)]
    public async Task Execute_BoundNodeOnRight_ShouldExpandLinearlyAsync(string later, bool optional)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = GraphDatabaseEngine.Create("bound-node-anchor", new());
        var database = await engine.CreateDatabaseAsync("optional", cancellation.Token);
        await using var session = await database.CreateSessionAsync(cancellation.Token);
        await session.ExecuteAsync("INSERT " + string.Join(",", Enumerable.Repeat("(:N)", 1001)), cancellationToken: cancellation.Token);
        string source = "MATCH (a:N) " + later + " RETURN a, b";
        var plan = await PlanAsync(database, session, source);
        // Act
        int expansions = await CountCandidatesAsync(database, session, plan, cancellation.Token);
        // Assert
        expansions.ShouldBe(2002);
        plan.MatchClauses.SelectMany(clause => clause.Paths).Last().Anchor.NodeIndex.ShouldBe(1);
        var rows = await QueryAsync(session, source);
        rows.Count.ShouldBe(optional ? 1001 : 0);
        rows.ShouldAllBe(row => row[0] is GraphNode && row[1] == null);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MATCH: a bound node precedes a less selective property index")]
    [InlineData("OPTIONAL MATCH", "(b:N {k:1})->(a)", 1, true)]
    [InlineData("MATCH", "(b:N {k:1})->(a)", 1, false)]
    [InlineData("OPTIONAL MATCH", "(a)->(b:N {k:1})", 0, true)]
    [InlineData("MATCH", "(a)->(b:N {k:1})", 0, false)]
    public async Task Execute_BoundNodeWithIndex_ShouldAvoidRepeatedSeeksAsync(string clause, string pattern, int position, bool optional)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = GraphDatabaseEngine.Create("bound-index-anchor", new());
        var database = await engine.CreateDatabaseAsync("optional", cancellation.Token);
        await using var session = await database.CreateSessionAsync(cancellation.Token);
        await session.ExecuteAsync("INSERT " + string.Join(",", Enumerable.Repeat("(:N {k:1})", 1001)), cancellationToken: cancellation.Token);
        await GraphSchema.Open(database, session).CreateIndexAsync("N", "by_k", "k", cancellation.Token);
        string source = "MATCH (a:N) " + clause + " " + pattern + " RETURN a, b";
        var plan = await PlanAsync(database, session, source);
        // Act
        int expansions = await CountCandidatesAsync(database, session, plan, cancellation.Token);
        var rows = await QueryAsync(session, source);
        // Assert
        expansions.ShouldBe(2002);
        var anchor = plan.MatchClauses[1].Paths[0].Anchor;
        anchor.NodeIndex.ShouldBe(position);
        anchor.Property.ShouldBeNull();
        rows.Count.ShouldBe(optional ? 1001 : 0);
        rows.ShouldAllBe(row => row[0] is GraphNode && row[1] == null);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - OPTIONAL MATCH: an indexed WHERE on outer variables preserves every outer row")]
    [InlineData("(a)-[:KNOWS]->(b)")]
    [InlineData("(b:Person {name:'Bob'})")]
    public async Task Execute_OptionalOuterPredicate_ShouldKeepOuterRowsAsync(string pattern)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-outer-filter", new());
        var database = await engine.CreateDatabaseAsync("optional", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        await GraphSchema.Open(database, session).CreateIndexAsync("Person", "by_name", "name", CancellationToken.None);
        string source = "MATCH (a:Person) OPTIONAL MATCH " + pattern + " WHERE a.name = 'Alice' RETURN a.name, b.name";
        // Act
        var plan = await PlanAsync(database, session, source);
        var rows = await QueryAsync(session, source);
        // Assert
        plan.MatchClauses[0].Paths[0].Anchor.Property.ShouldBeNull();
        if (pattern.StartsWith("(a)", StringComparison.Ordinal))
        { plan.MatchClauses[1].Paths[0].Anchor.Property.ShouldBeNull(); }
        rows.Select(row => row[0]).ShouldBe(new object?[] { "Alice", "Bob", "Isolated" });
        rows.Select(row => row[1]).ShouldBe(new object?[] { "Bob", null, null });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - MATCH: a bound relationship supplies adjacent endpoints before scans or seeks")]
    [InlineData("KNOWS", "MATCH (x:Person {name:'Alice'})-[r]->(y)", "Alice,Bob", 0)]
    [InlineData("KNOWS", "MATCH (x)<-[r]-(y)", "Bob,Alice", 0)]
    [InlineData("KNOWS", "MATCH (x)-[r]-(y)", "Alice,Bob;Bob,Alice", 0)]
    [InlineData("KNOWS", "MATCH (x)<-[r]->(y)", "Alice,Bob;Bob,Alice", 0)]
    [InlineData("KNOWS", "MATCH (x)-[:LINK]->()-[r]->(y)", "Before,Bob", 1)]
    [InlineData("KNOWS", ", (x)-[:LINK]->()-[r]->(y)", "Before,Bob", 1)]
    [InlineData("KNOWS", "OPTIONAL MATCH (x)-[:LINK]->()-[r]->(y)", "Before,Bob", 1)]
    [InlineData("LOOP", "MATCH (x)-[r]-(y)", "Self,Self", 0)]
    public async Task Execute_BoundRelationship_ShouldAnchorAtEndpointAsync(string type, string later, string expected, int position)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("bound-edge-anchor", new());
        var database = await engine.CreateDatabaseAsync("optional", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("MATCH (a:Person {name:'Alice'}) INSERT (:Person {name:'Before'})-[:LINK]->(a)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT (s:Person {name:'Self'})-[:LOOP]->(s)", cancellationToken: CancellationToken.None);
        await GraphSchema.Open(database, session).CreateIndexAsync("Person", "by_name", "name", CancellationToken.None);
        string source = "MATCH ()-[r:" + type + "]->() " + later + " RETURN x.name, y.name";
        // Act
        var plan = await PlanAsync(database, session, source);
        int expansions = await CountCandidatesAsync(database, session, plan, CancellationToken.None);
        var rows = await QueryAsync(session, source);
        // Assert
        var anchor = plan.MatchClauses.SelectMany(clause => clause.Paths).Last().Anchor;
        anchor.NodeIndex.ShouldBe(position);
        anchor.Property.ShouldBeNull();
        expansions.ShouldBeLessThanOrEqualTo(15);
        rows.Select(row => string.Join(",", row)).ShouldBe(expected.Split(';'));
    }

    // Measure each input separately so a regression reports its full candidate count, even when
    // the statement's shared budget would stop execution before all 1,001 inputs were visited.
    private static ValueTask<int> CountCandidatesAsync(GraphDatabase database, GraphDatabaseSession session, GraphPlan plan, CancellationToken token)
        => database.RunAsync(session, async operation =>
        {
            var paths = plan.MatchClauses.SelectMany(clause => clause.Paths).ToArray();
            var input = new List<Dictionary<string, object?>>();
            var first = new GraphPlanExecutor.ExpansionBudget();
            await GraphPlanExecutor.MatchAsync(database, operation, paths[0], new(StringComparer.Ordinal), input, first, null, token);
            int consumed = first.Consumed;
            foreach (var binding in input)
            {
                var budget = new GraphPlanExecutor.ExpansionBudget();
                await GraphPlanExecutor.MatchAsync(database, operation, paths[1], binding, [], budget, null, token);
                consumed += budget.Consumed;
            }
            return consumed;
        }, token);

    private static ValueTask<GraphPlan> PlanAsync(GraphDatabase database, GraphDatabaseSession session, string source, bool paths = false)
        => database.RunAsync(session, operation => ValueTask.FromResult(
            new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(source).Statement.GqlExpression, paths: paths)), CancellationToken.None);

    private static async Task<List<object?[]>> QueryAsync(GraphDatabaseSession session, string source)
    {
        await using var result = (await session.ExecuteAsync(source, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await RowsAsync(result);
    }

    private static async Task<List<object?[]>> RowsAsync(DatabaseResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
