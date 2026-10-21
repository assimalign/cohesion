using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphWhereLabelAnchorTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - WHERE labels: scans charge only the required label's candidates (#1245)")]
    [InlineData("n:Keep")]
    [InlineData("n IS LABELED Keep")]
    [InlineData("TRUE AND (n IS LABELED Keep&Tagged AND n.k >= 0)")]
    public async Task MatchAsync_RequiredWhereLabel_ShouldChargeOnlyLabeledCandidatesAsync(string predicate)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await using var engine = GraphDatabaseEngine.Create("where-label", new());
        var database = await engine.CreateDatabaseAsync("graph", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await session.ExecuteAsync("FOR k IN [" + string.Join(',', Enumerable.Range(0, 2_000)) + "] INSERT (:Noise {k:k})", cancellationToken: timeout.Token);
        await session.ExecuteAsync("FOR k IN [0,1,2,3,4,5,6,7,8,9] INSERT (:Keep:Tagged {k:k})", cancellationToken: timeout.Token);
        string source = "MATCH (n) WHERE " + predicate + " RETURN n.k";
        var budget = new GraphPlanExecutor.ExpansionBudget();

        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(source).Statement.GqlExpression);
            await GraphPlanExecutor.MatchAsync(database, operation, plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), [], budget, null, timeout.Token);
            return true;
        }, timeout.Token);
        var rows = await RowsAsync(session, source, timeout.Token);
        var description = await RowsAsync(session, "EXPLAIN " + source, timeout.Token);

        // Assert
        budget.Consumed.ShouldBe(10);
        rows.Select(row => row[0]).ShouldBe(Enumerable.Range(0, 10).Select(value => (object?)(long)value), ignoreOrder: true);
        var scan = description.Single(row => Equals(row[2], "LabelScan"));
        scan[3].ShouldBe("Keep");
        scan[5].ShouldBe("node=0; property=; value=NULL");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Label scans: pattern and WHERE labels anchor at later node positions (#1245)")]
    [InlineData("MATCH (a)->(b:Keep) RETURN a.k,b.k", 1, 2, "0,1")]
    [InlineData("MATCH (a)->(b) WHERE b:Keep RETURN a.k,b.k", 1, 2, "0,1")]
    [InlineData("MATCH (a)->(b:Keep)->(c) RETURN a.k,b.k,c.k", 1, 3, "0,1,2")]
    [InlineData("MATCH (a)->(b)->(c) WHERE b IS LABELED Keep RETURN a.k,b.k,c.k", 1, 3, "0,1,2")]
    [InlineData("MATCH (a)->(b)->(c:End) RETURN a.k,b.k,c.k", 2, 3, "0,1,2")]
    [InlineData("MATCH (a)->(b)->(c) WHERE c:End RETURN a.k,b.k,c.k", 2, 3, "0,1,2")]
    public async Task MatchAsync_LaterRequiredLabel_ShouldAnchorAndPreservePathOrderAsync(string source, int position, int consumed, string expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("later-label", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Start {k:0})-[:T]->(:Keep {k:1})-[:T]->(:End {k:2}), (:Other)", cancellationToken: CancellationToken.None);
        var budget = new GraphPlanExecutor.ExpansionBudget();

        // Act
        await database.RunAsync(session, async operation =>
        {
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(GraphQueryRequest.FromGql(source).Statement.GqlExpression);
            plan.MatchClauses[0].Paths[0].Anchor.NodeIndex.ShouldBe(position);
            await GraphPlanExecutor.MatchAsync(database, operation, plan.MatchClauses[0].Paths[0], new(StringComparer.Ordinal), [], budget, null, CancellationToken.None);
            return true;
        }, CancellationToken.None);
        var rows = await RowsAsync(session, source, CancellationToken.None);
        var description = await RowsAsync(session, "EXPLAIN " + source, CancellationToken.None);
        string pathSource = source[..source.IndexOf(" RETURN", StringComparison.Ordinal)]
            .Replace("MATCH ", "MATCH p = ", StringComparison.Ordinal) + " RETURN p";
        var paths = (await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(pathSource), CancellationToken.None))
            .ShouldBeOfType<GraphPathsQueryResult>();

        // Assert
        budget.Consumed.ShouldBe(consumed);
        string.Join(',', rows.ShouldHaveSingleItem()).ShouldBe(expected);
        description.Single(row => Equals(row[2], "LabelScan"))[5].ShouldBe($"node={position}; property=; value=NULL");
        paths.Paths.ShouldHaveSingleItem().Nodes.Select(node => node.Properties["k"])
            .ShouldBe(expected.Split(',').Select(value => (object?)(long)int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - WHERE labels: all required labels participate in index selection and retain residuals (#1245)")]
    [InlineData("MATCH (n) WHERE n:Keep AND n:Tagged AND n.k=$key RETURN n.k", 0)]
    [InlineData("MATCH (n:Keep) WHERE n:Tagged AND $key=n.k RETURN n.k", 0)]
    [InlineData("MATCH (n {k:$key}) WHERE n IS LABELED Keep&Tagged RETURN n.k", 0)]
    [InlineData("MATCH (n) WHERE TRUE AND (n:Keep AND (n.k=$key AND n:Tagged)) RETURN n.k", 0)]
    [InlineData("MATCH (a:Keep)->(n) WHERE n:Keep AND n:Tagged AND n.k=$key RETURN n.k", 1)]
    public async Task Execute_RequiredWhereLabels_ShouldSeekIndexedConjunctAsync(string source, int position)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("where-index", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Keep {k:3})-[:T]->(:Keep:Tagged {k:1}), (:Tagged {k:1}), (:Keep:Tagged {k:2})", cancellationToken: CancellationToken.None);
        await GraphSchema.Open(database, session).CreateIndexAsync("Tagged", "by_k", "k", CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["key"] = 1L };

        // Act
        var rows = await RowsAsync(session, source, CancellationToken.None, parameters);
        var description = await RowsAsync(session, "EXPLAIN " + source, CancellationToken.None, parameters);

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([1L]);
        var seek = description.Single(row => Equals(row[2], "IndexSeek"));
        seek[3].ShouldBe("Tagged");
        seek[4].ShouldBe("by_k");
        seek[5].ShouldBe($"node={position}; property=k; value=1");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - WHERE labels: only positive conjunctions supply node anchors (#1245)")]
    [InlineData("n IS NOT LABELED Keep", null)]
    [InlineData("NOT n:Keep", null)]
    [InlineData("n:Keep OR n:Tagged", null)]
    [InlineData("n:Keep XOR n:Tagged", null)]
    [InlineData("n IS LABELED Keep|Tagged", null)]
    [InlineData("n IS LABELED Keep&!Tagged", null)]
    [InlineData("n IS LABELED %", null)]
    [InlineData("(n:Keep) IS FALSE", null)]
    [InlineData("(n:Keep OR n:Tagged) AND n:Tagged", "Tagged")]
    [InlineData("n:Keep AND (n IS NOT LABELED Tagged OR TRUE)", "Keep")]
    public async Task Execute_NonRequiredLabels_ShouldRetainSafeScanAsync(string predicate, string? label)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("safe-label", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Keep {k:0}), (:Tagged {k:1}), (:Keep:Tagged {k:2}), ({k:3})", cancellationToken: CancellationToken.None);
        string source = "MATCH (n) WHERE " + predicate + " RETURN n.k";

        // Act
        var description = await RowsAsync(session, "EXPLAIN " + source, CancellationToken.None);
        var rows = await RowsAsync(session, source, CancellationToken.None);
        var reference = await RowsAsync(session, "MATCH (n) WITH n WHERE " + predicate + " RETURN n.k", CancellationToken.None);

        // Assert
        var scan = description.Single(row => row[2] is "NodeScan" or "LabelScan");
        scan[3].ShouldBe(label);
        rows.Select(row => row[0]).ShouldBe(reference.Select(row => row[0]), ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - WHERE labels: clause scope, relationship kinds and bound anchors stay distinct (#1245)")]
    public async Task Execute_ClauseLocalLabels_ShouldRespectKindsAndBoundAnchorsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("scoped-label", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Start {k:0})-[:Keep]->(:Keep {k:1})-[:Keep]->(:End {k:2})", cancellationToken: CancellationToken.None);

        // Act
        var relationship = await RowsAsync(session, "EXPLAIN MATCH (n)-[r]->(m) WHERE r:Keep RETURN n.k,m.k", CancellationToken.None);
        var rebound = await RowsAsync(session, "EXPLAIN MATCH (n) WHERE n:Keep WITH n.k AS k MATCH (n) RETURN n.k", CancellationToken.None);
        var bound = await RowsAsync(session, "EXPLAIN MATCH (a:Start)->(b) OPTIONAL MATCH (b)->(c) WHERE c:Keep RETURN a.k,b.k,c.k", CancellationToken.None);
        var rows = await RowsAsync(session, "MATCH (a:Start)->(b) OPTIONAL MATCH (b)->(c) WHERE c:Keep RETURN a.k,b.k,c.k", CancellationToken.None);

        // Assert
        relationship.Count(row => Equals(row[2], "NodeScan")).ShouldBe(1);
        rebound.Count(row => Equals(row[2], "LabelScan")).ShouldBe(1);
        rebound.Count(row => Equals(row[2], "NodeScan")).ShouldBe(1);
        bound.Where(row => row[2] is "NodeScan" or "LabelScan").Last()[5].ShouldBe("node=0; property=; value=NULL");
        rows.ShouldHaveSingleItem().ShouldBe([0L, 1L, null]);
    }

    private static async Task<List<object?[]>> RowsAsync(GraphDatabaseSession session, string source, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await session.ExecuteAsync(source, parameters, cancellationToken)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(cancellationToken))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
