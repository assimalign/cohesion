using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphQueryTests
{
    [Theory]
    [InlineData("INSERT")]
    [InlineData("CREATE")]
    public async Task InsertionProjectionAndPropertyPredicates_ExecuteEveryAdvertisedClause(string verb)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("queries");
        await using var session = await database.CreateSessionAsync();
        var created = await Rows(session, $"{verb} (a:Person {{name: 'Alice', age: 42, active: TRUE, optional: NULL}}) RETURN a");
        var alice = created.Single().GetValue(0).ShouldBeOfType<GraphNode>();
        alice.Labels.ShouldBe(["Person"]);
        alice.Properties["age"].ShouldBe(42L);
        (await database.GetNodeAsync(session, alice.Id)).ShouldNotBeNull();
        await session.ExecuteAsync("INSERT (:Person {name: 'Bob', age: 17, active: FALSE})");
        await using var result = (QueryResultSet)await session.ExecuteAsync(
            "MATCH (a:Person) WHERE a.age >= 18 AND a.age <= 42 AND a.age > 17 AND a.age < 43 AND a.name <> 'Bob' AND a.active = TRUE RETURN a.name AS person, a.age, a.optional");
        result.Columns.Select(column => column.Name).ShouldBe(["person", "a.age", "a.optional"]);
        var rows = new List<QueryRow>();
        await foreach (var row in result.GetRowsAsync()) { rows.Add(row); }
        rows.Count.ShouldBe(1);
        rows[0].GetString(0).ShouldBe("Alice");
        rows[0].GetInt64(1).ShouldBe(42L);
        rows[0].IsNull(2).ShouldBeTrue();
        (await Rows(session, "MATCH (a:Person) WHERE a.optional = NULL RETURN a")).ShouldBeEmpty();
        var deleted = await session.ExecuteAsync("MATCH (a:Person {name: 'Bob'}) DELETE a");
        deleted.AffectedCount.ShouldBe(1);
        (await Rows(session, "MATCH (a:Person) RETURN a.name")).Single().GetString(0).ShouldBe("Alice");
    }

    [Theory]
    [InlineData("(a:Person {name: 'Alice'})-[r:KNOWS]->(b)", "Alice", "Bob")]
    [InlineData("(a:Person {name: 'Bob'})<-[r:KNOWS]-(b)", "Bob", "Alice")]
    [InlineData("(a:Person {name: 'Bob'})-[r:KNOWS]-(b)", "Bob", "Alice")]
    public async Task PatternDirections_PreserveStoredRelationshipEndpoints(string pattern, string first, string second)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("directions");
        await using var session = await database.CreateSessionAsync();
        var inserted = (await Rows(session, "INSERT (a:Person {name: 'Alice'})-[r:KNOWS {weight: 2}]->(b:Person {name: 'Bob'}) RETURN a, r, b")).Single();
        var alice = inserted.GetValue(0).ShouldBeOfType<GraphNode>();
        var relationship = inserted.GetValue(1).ShouldBeOfType<GraphRelationship>();
        var bob = inserted.GetValue(2).ShouldBeOfType<GraphNode>();
        relationship.From.ShouldBe(alice.Id);
        relationship.To.ShouldBe(bob.Id);
        var row = (await Rows(session, $"MATCH {pattern} WHERE r.weight = 2 RETURN a.name, r, b.name")).Single();
        row.GetString(0).ShouldBe(first);
        row.GetValue(1).ShouldBeOfType<GraphRelationship>().Id.ShouldBe(relationship.Id);
        row.GetString(2).ShouldBe(second);
        (await Rows(session, "MATCH (a:Person {name: 'Bob'})-[r:KNOWS]->(b) RETURN b")).ShouldBeEmpty();
    }

    [Fact]
    public async Task MatchCreate_ReusesBoundEndpointsAndDoesNotDuplicateNodes()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("bound");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Person {name: 'Alice'}), (:Person {name: 'Bob'})");
        var row = (await Rows(session,
            "MATCH (a:Person {name: 'Alice'}), (b:Person {name: 'Bob'}) CREATE (a)-[r:KNOWS {since: 2026}]->(b) RETURN a, r, b")).Single();
        var edge = row.GetValue(1).ShouldBeOfType<GraphRelationship>();
        edge.From.ShouldBe(row.GetValue(0).ShouldBeOfType<GraphNode>().Id);
        edge.To.ShouldBe(row.GetValue(2).ShouldBeOfType<GraphNode>().Id);
        (await Rows(session, "MATCH (a:Person) RETURN a")).Count.ShouldBe(2);
        (await Rows(session, "MATCH (a)-[r:KNOWS]->(b) RETURN r.since")).Single().GetInt64(0).ShouldBe(2026L);
    }

    [Fact]
    public async Task FiniteMatch_OnCycleAllowsRepeatedNodesAndNeverRepeatsARelationship()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("cycle");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (a:Vertex {name: 'A'})-[:LINK]->(b:Vertex {name: 'B'})-[:LINK]->(c:Vertex {name: 'C'})-[:LINK]->(a)");
        var rows = await Rows(session,
            "MATCH (a:Vertex {name: 'A'})-[r:LINK]->(b)-[s:LINK]->(c)-[t:LINK]->(a) RETURN a.name, b.name, c.name, r, s, t")
            .WaitAsync(TimeSpan.FromSeconds(5));
        rows.Count.ShouldBe(1);
        Enumerable.Range(0, 3).Select(rows[0].GetString).ShouldBe(["A", "B", "C"]);
        Enumerable.Range(3, 3).Select(index => rows[0].GetValue(index).ShouldBeOfType<GraphRelationship>().Id).Distinct().Count().ShouldBe(3);
        (await Rows(session,
            "MATCH (a:Vertex {name: 'A'})-[:LINK]->(b)-[:LINK]->(c)-[:LINK]->(d)-[:LINK]->(e) RETURN e")
            .WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeEmpty();
        (await Rows(session, "MATCH (a:Vertex {name: 'A'})-[:LINK]->(a) RETURN a")).ShouldBeEmpty();
    }

    [Fact]
    public async Task SelfLoop_IsEmittedOnceAndCannotFillTwoHops()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("loop");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (a:Vertex {name: 'A'})-[r:LINK]->(a)");
        (await Rows(session, "MATCH (a)-[r:LINK]-(a) RETURN a, r")).Count.ShouldBe(1);
        (await Rows(session, "MATCH (a)-[r:LINK]->(b)-[s:LINK]->(c) RETURN c")).ShouldBeEmpty();
        (await Rows(session, "MATCH (a)-[r:LINK]->(b)-[r:LINK]->(c) RETURN c")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("MATCH (a)-[:LINK]->(b:Vertex {name: 'B'})-[:LINK]->(c) RETURN a.name, b.name, c.name")]
    [InlineData("MATCH (a)-[:LINK]->(b:Vertex)-[:LINK]->(c) WHERE b.name = 'B' RETURN a.name, b.name, c.name")]
    [InlineData("MATCH (a)-[:LINK]->(b:Vertex)-[:LINK]->(c) WHERE 'B' = b.name RETURN a.name, b.name, c.name")]
    public async Task MultiHopPlan_AnchorsAtAnIndexedMiddleNodeAndReturnsCorrectPath(string query)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("indexed");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (a:Vertex {name: 'A'})-[:LINK]->(b:Vertex {name: 'B'})-[:LINK]->(c:Vertex {name: 'C'}), (:Vertex {name: 'D'})");
        (await Plan(database, session, query)).Matches.Single().Anchor.Property.ShouldBeNull();
        await GraphSchema.Open(database, session).CreateIndexAsync("Vertex", "by_name", "name");
        var anchor = (await Plan(database, session, query)).Matches.Single().Anchor;
        anchor.NodeIndex.ShouldBe(1);
        anchor.Label.ShouldBe("Vertex");
        anchor.Property.ShouldBe("name");
        anchor.Value.ShouldBe("B");
        var row = (await Rows(session, query)).Single();
        Enumerable.Range(0, 3).Select(row.GetString).ShouldBe(["A", "B", "C"]);
    }

    [Fact]
    public async Task PropertyIndex_IsMaintainedForEveryInsertionAndDeletionPath()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("maintained");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Person {name: 'seed'})");
        await GraphSchema.Open(database, session).CreateIndexAsync("Person", "by_name", "name");
        var direct = await database.CreateNodeAsync(session, ["Person"], new Dictionary<string, object?> { ["name"] = "direct" });
        await session.ExecuteAsync("CREATE (:Person {name: 'create'})");
        await session.ExecuteAsync("INSERT (:Person {name: 'insert'})");
        foreach (string value in new[] { "direct", "create", "insert" })
        {
            string query = $"MATCH (a:Person {{name: '{value}'}}) RETURN a.name";
            (await Plan(database, session, query)).Matches.Single().Anchor.Property.ShouldBe("name");
            (await Rows(session, query)).Single().GetString(0).ShouldBe(value);
        }
        await database.DeleteNodeAsync(session, direct.Id);
        await session.ExecuteAsync("MATCH (a:Person {name: 'create'}) DELETE a");
        await session.ExecuteAsync("MATCH (a:Person {name: 'insert'}) DETACH DELETE a");
        foreach (string value in new[] { "direct", "create", "insert" })
        {
            (await Rows(session, $"MATCH (a:Person {{name: '{value}'}}) RETURN a")).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task RestrictedDelete_RollsBackEarlierDeletesInTheSameStatement()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("restricted");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Person {name: 'isolated'}), (a:Person {name: 'connected'})-[:LINK]->(b:Other {name: 'target'})");
        var exception = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (a:Person) DELETE a"));
        exception.Message.ShouldContain("COHDBG");
        (await Rows(session, "MATCH (a:Person) RETURN a.name")).Select(row => row.GetString(0)).Order().ShouldBe(["connected", "isolated"]);
        (await Rows(session, "MATCH (a)-[r:LINK]->(b) RETURN r")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task DetachDelete_IsAtomicWithEndpointAdjacencyAndRollback()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("detach");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (a:Vertex {name: 'A'})-[:LINK]->(b:Vertex {name: 'B'})-[:LINK]->(c:Vertex {name: 'C'}), (b)-[:LINK]->(b)");
        var target = (await Rows(session, "MATCH (b:Vertex {name: 'B'}) RETURN b")).Single().GetValue(0).ShouldBeOfType<GraphNode>();
        await using (var transaction = await session.BeginTransactionAsync())
        {
            await session.ExecuteAsync("MATCH (b:Vertex {name: 'B'}) DETACH DELETE b");
            (await database.GetNodeAsync(session, target.Id)).ShouldBeNull();
            (await Rows(session, "MATCH (a)-[r:LINK]->(b) RETURN r")).ShouldBeEmpty();
            await transaction.RollbackAsync();
        }
        (await database.GetNodeAsync(session, target.Id)).ShouldNotBeNull();
        (await Rows(session, "MATCH (a)-[r:LINK]->(b) RETURN r")).Count.ShouldBe(3);
        await session.ExecuteAsync("MATCH (b:Vertex {name: 'B'}) DETACH DELETE b");
        (await database.GetNodeAsync(session, target.Id)).ShouldBeNull();
        (await Rows(session, "MATCH (a)-[r:LINK]->(b) RETURN r")).ShouldBeEmpty();
        (await Rows(session, "MATCH (a:Vertex) RETURN a.name")).Select(row => row.GetString(0)).Order().ShouldBe(["A", "C"]);
    }

    [Fact]
    public async Task ExplicitRelationshipDelete_AllowsRestrictedNodeDelete()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("explicit");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (a:Vertex {name: 'A'})-[r:LINK]->(b:Vertex {name: 'B'})");
        var result = await session.ExecuteAsync("MATCH (a:Vertex {name: 'A'})-[r:LINK]->(b) DELETE a, r");
        result.AffectedCount.ShouldBe(2);
        (await Rows(session, "MATCH (a:Vertex) RETURN a.name")).Single().GetString(0).ShouldBe("B");
    }

    [Theory]
    [InlineData("MATCH (a)-[a]->(b) RETURN a", "COHDBG003")]
    [InlineData("MATCH (a) RETURN unbound", "COHDBG001")]
    [InlineData("INSERT (a)-[r]-(b)", "COHDBG001")]
    [InlineData("INSERT (a)-[r]->(b)", "COHDBG001")]
    public async Task BindingAndSchemaDiagnostics_HaveStableCodes(string query, string code)
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("diagnostics");
        await using var session = await database.CreateSessionAsync();
        var exception = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query));
        exception.Message.ShouldContain(code);
        (await Rows(session, "MATCH (a) RETURN a")).ShouldBeEmpty();
    }

    [Fact]
    public async Task DirectParsedRequest_CannotBypassUnsupportedClauseDiagnostics()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("capability");
        await using var session = await database.CreateSessionAsync();
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse("MATCH (a) RETURN a LIMIT 2");
        var exception = await Should.ThrowAsync<DatabaseParseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(parsed)));
        exception.Message.ShouldContain("COHDBL001");
    }

    [Fact]
    public async Task FiniteFloatingPointLiterals_AreComparableAcrossTheirAcceptedRange()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("numeric");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Number {value: 1e100}), (:Number {value: 1e-100}), (:Number {value: 2e-100})");
        (await Rows(session, "MATCH (n:Number) WHERE n.value = 1e100 AND n.value > 1 RETURN n.value")).Single().GetDouble(0).ShouldBe(1e100);
        (await Rows(session, "MATCH (n:Number) WHERE n.value = 1e-100 RETURN n.value")).Single().GetDouble(0).ShouldBe(1e-100);
        await GraphSchema.Open(database, session).CreateIndexAsync("Number", "by_value", "value");
        await session.ExecuteAsync("INSERT (:Number {value: 1e101})");
        foreach (var literal in new[] { "1e100", "1e101", "1e-100", "2e-100" })
        {
            string query = $"MATCH (n:Number) WHERE n.value = {literal} RETURN n.value";
            (await Plan(database, session, query)).Matches.Single().Anchor.Property.ShouldBe("value");
            (await Rows(session, query)).Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task NumericIndexCandidateCollisions_PreserveExactIntegerEquality()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("integer-collision");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("INSERT (:Number {value: 9007199254740992}), (:Number {value: 9007199254740993})");
        await GraphSchema.Open(database, session).CreateIndexAsync("Number", "by_value", "value");
        foreach (long value in new[] { 9007199254740992L, 9007199254740993L })
        {
            string query = $"MATCH (n:Number) WHERE n.value = {value} RETURN n.value";
            (await Plan(database, session, query)).Matches.Single().Anchor.Property.ShouldBe("value");
            (await Rows(session, query)).Single().GetInt64(0).ShouldBe(value);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Direct AST: direction and shape rules cannot be bypassed")]
    public async Task Execute_DirectAst_ShouldEnforceDirectionAndShapeRules()
    {
        await using var engine = GraphDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("ast-bounds");
        await using var session = await database.CreateSessionAsync();
        GqlNodePattern node = new("a", [], new Dictionary<string, object?>());
        GqlRelationshipPattern edge = new("r", null, (GqlPatternDirection)99, new Dictionary<string, object?>());
        var malformed = new GqlQueryStatement(new GqlQueryExpression([new GqlPathPattern([node, node], [edge])], null, [], [], false, [new GqlProjection("a")]));
        var invalid = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(malformed)));
        invalid.Message.ShouldContain("COHDBG001");

        // Predicates (#1139 follow-up): AND is one n-ary GqlLogicalExpression, so a binary AND,
        // an undefined operator, a missing operand or list, and a chain of fewer than two operands
        // are COHDBG001. Depth has no fixed limit (GqlLabelChainExecutionTests).
        GqlExpression one = new GqlBinaryExpression(new GqlLiteralExpression(1L), "=", new GqlLiteralExpression(1L));
        GqlExpression[] badChains =
        [
            new GqlBinaryExpression(one, "AND", new GqlLiteralExpression(true)),
            new GqlLogicalExpression((GqlLogicalOperator)7, [one, one]),
            new GqlLogicalExpression(GqlLogicalOperator.And, [one]),
            new GqlLogicalExpression(GqlLogicalOperator.And, null!),
            new GqlLogicalExpression(GqlLogicalOperator.And, [one, null!]),
            new GqlBinaryExpression(null!, "=", new GqlLiteralExpression(1L)),
        ];
        foreach (var badChain in badChains)
        {
            var statement = new GqlQueryStatement(new GqlQueryExpression([new GqlPathPattern([node], [])], badChain, [], [], false, [new GqlProjection("a")]));
            (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(statement))))
                .Message.ShouldStartWith("COHDBG001", Case.Sensitive);
        }

        // Label expressions (#1139): a null operand, name or operand list, a chain of fewer than
        // two operands, a labeled predicate without an expression, and Labels or Type that
        // disagree with the expression.
        await session.ExecuteAsync("INSERT (:A {k: 1})-[:T]->(:B)");
        var empty = new Dictionary<string, object?>();
        GqlNodePattern[] nodes =
        [
            new("a", [], empty) { LabelExpression = new GqlLabelNegation(null!) },
            new("a", [], empty) { LabelExpression = new GqlLabelDisjunction([new GqlLabelName("A"), null!]) },
            new("a", [], empty) { LabelExpression = new GqlLabelName(null!) },
            new("a", [], empty) { LabelExpression = new GqlLabelConjunction([new GqlLabelName("A")]) },
            new("a", [], empty) { LabelExpression = new GqlLabelDisjunction([]) },
            new("a", [], empty) { LabelExpression = new GqlLabelConjunction(null!) },
            new("a", ["B"], empty) { LabelExpression = new GqlLabelName("A") },
            new("a", ["A"], empty) { LabelExpression = new GqlLabelDisjunction([new GqlLabelName("A"), new GqlLabelName("B")]) },
            new("a", ["A"], null!),
        ];
        foreach (var badNode in nodes)
        {
            var statement = new GqlQueryStatement(new GqlQueryExpression([new GqlPathPattern([badNode], [])], null, [], [], false, [new GqlProjection("a")]));
            (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(statement))))
                .Message.ShouldStartWith("COHDBG001", Case.Sensitive);
        }
        GqlRelationshipPattern[] edges =
        [
            new("r", null, GqlPatternDirection.Outgoing, empty) { LabelExpression = new GqlLabelConjunction([null!, new GqlLabelName("T")]) },
            new("r", null, GqlPatternDirection.Outgoing, empty) { LabelExpression = new GqlLabelDisjunction([new GqlLabelName("T")]) },
            new("r", "U", GqlPatternDirection.Outgoing, empty) { LabelExpression = new GqlLabelName("T") },
            new("r", "T", GqlPatternDirection.Outgoing, empty) { LabelExpression = new GqlLabelNegation(new GqlLabelName("T")) },
            new("r", null, (GqlPatternDirection)4, empty),
            new("r", "T", GqlPatternDirection.Outgoing, null!),
        ];
        foreach (var badEdge in edges)
        {
            var statement = new GqlQueryStatement(new GqlQueryExpression([new GqlPathPattern([node, new("b", [], empty)], [badEdge])], null, [], [], false, [new GqlProjection("a")]));
            (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(statement))))
                .Message.ShouldStartWith("COHDBG001", Case.Sensitive);
        }
        GqlExpression[] predicates =
        [
            new GqlLabeledPredicate("a", null!),
            new GqlLabeledPredicate(null!, new GqlLabelName("A")),
            new GqlLabeledPredicate("a", new GqlLabelConjunction([new GqlLabelName("A")])),
            new GqlLogicalExpression(GqlLogicalOperator.And, [one, new GqlLabeledPredicate("a", new GqlLabelDisjunction(null!))]),
        ];
        foreach (var badPredicate in predicates)
        {
            var statement = new GqlQueryStatement(new GqlQueryExpression([new GqlPathPattern([node], [])], badPredicate, [], [], false, [new GqlProjection("a")]));
            (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(statement))))
                .Message.ShouldStartWith("COHDBG001", Case.Sensitive);
        }

        // Storage cannot hold an empty label, type or property key, so a hand-built insertion
        // naming one is COHDBG001 before anything is written.
        GqlPathPattern[] unstorable =
        [
            new([new(null, [""], empty)], []),
            new([new(null, [], empty) { LabelExpression = new GqlLabelConjunction([new GqlLabelName("A"), new GqlLabelName(" ")]) }], []),
            new([new(null, ["A"], new Dictionary<string, object?> { [""] = 1L })], []),
            new([new(null, ["A"], empty), new(null, ["B"], empty)], [new(null, "", GqlPatternDirection.Outgoing, empty)]),
            new([new(null, ["A"], empty), new(null, ["B"], empty)],
                [new(null, "T", GqlPatternDirection.Incoming, new Dictionary<string, object?> { ["\t"] = 1L })]),
        ];
        foreach (var insertion in unstorable)
        {
            var statement = new GqlQueryStatement(new GqlQueryExpression([], null, [insertion], [], false, []));
            (await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(statement))))
                .Message.ShouldStartWith("COHDBG001", Case.Sensitive);
        }
        (await Rows(session, "MATCH (n) RETURN n")).Count.ShouldBe(2);

        // A hand-built pattern with Labels alone keeps its meaning, and a consistent pair agrees.
        GqlNodePattern legacy = new("a", ["A"], empty);
        GqlNodePattern paired = new("a", ["A"], empty) { LabelExpression = new GqlLabelName("A") };
        foreach (var pattern in new[] { legacy, paired })
        {
            var statement = new GqlQueryStatement(new GqlQueryExpression([new GqlPathPattern([pattern], [])], null, [], [], false, [new GqlProjection("a", "k")]));
            await using var result = (QueryResultSet)await session.ExecuteAsync(new GraphQueryRequest(statement));
            var values = new List<object?>();
            await foreach (var row in result.GetRowsAsync()) { values.Add(row.GetValue(0)); }
            values.ShouldBe([1L]);
        }
    }

    private static async Task<List<QueryRow>> Rows(GraphDatabaseSession session, string query)
    {
        await using var result = (QueryResultSet)await session.ExecuteAsync(query);
        List<QueryRow> rows = [];
        await foreach (var row in result.GetRowsAsync()) { rows.Add(row); }
        return rows;
    }

    private static ValueTask<GraphPlan> Plan(GraphDatabase database, GraphDatabaseSession session, string query)
    {
        return database.RunAsync(session, operation => new ValueTask<GraphPlan>(
            new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(query).Statement.GqlExpression)), CancellationToken.None);
    }
}
