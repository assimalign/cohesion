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
