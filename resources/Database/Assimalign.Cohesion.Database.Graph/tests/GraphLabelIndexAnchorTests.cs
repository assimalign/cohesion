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

public sealed class GraphLabelIndexAnchorTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Label anchors: choose the smallest required label across pattern positions (#1534)")]
    public async Task Match_RequiredLabels_ShouldChooseSmallestPostingEstimateAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("label-cost", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("FOR k IN [0,1,2,3,4,5,6,7,8,9] INSERT (:Common {k:k})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Common {k:20})-[:T]->(:Common:Rare {k:21})", cancellationToken: CancellationToken.None);

        // Act / Assert: both inline and WHERE conjunctions keep all residual checks.
        foreach (string pattern in new[] { "MATCH (a:Common)->(b:Common&Rare)", "MATCH (a:Common)->(b) WHERE b:Common AND b:Rare" })
        {
            var description = await RowsAsync(session, "EXPLAIN " + pattern + " RETURN a.k,b.k");
            var scan = description.Single(row => Equals(row[2], "LabelScan"));
            scan[3].ShouldBe("Rare");
            scan[5].ShouldBe("node=1; property=; value=NULL");
            long before = database.Store.NodeRecordsDecoded;
            (await RowsAsync(session, pattern + " RETURN a.k,b.k")).ShouldHaveSingleItem().ShouldBe([20L, 21L]);
            (database.Store.NodeRecordsDecoded - before).ShouldBe(2); // One anchor and its adjacent node.
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Label unions: duplicate suppression and staged additions and removals use only selected ranges (#1534)")]
    public async Task Match_UnionAndOverlay_ShouldReadOnlySelectedLabelsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("label-union", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:A {k:1}), (:B {k:2}), (:A:B {k:3}), (:Noise {k:4})", cancellationToken: CancellationToken.None);

        // Act / Assert
        long before = database.Store.NodeRecordsDecoded;
        (await RowsAsync(session, "MATCH (n:A|B|A) RETURN n.k ORDER BY n.k")).Select(row => row[0]).ShouldBe([1L, 2L, 3L]);
        (database.Store.NodeRecordsDecoded - before).ShouldBe(3);
        (await RowsAsync(session, "MATCH (n:A&B) RETURN n.k")).ShouldHaveSingleItem().ShouldBe([3L]);
        (await RowsAsync(session, "MATCH (n:A {k:1}) REMOVE n:A SET n:B MATCH (m:A|B) RETURN m.k ORDER BY m.k"))
            .Select(row => row[0]).ShouldBe([1L, 2L, 3L]);
        (await RowsAsync(session, "INSERT (:A {k:5}) MATCH (n:A) RETURN n.k ORDER BY n.k"))
            .Select(row => row[0]).ShouldBe([3L, 5L]);

        await database.RunAsync(session, async operation =>
        {
            for (int i = 0; i < 100; i++)
            { await operation.Updates.CreateNodeAsync(["Noise"], new Dictionary<string, object?>(), CancellationToken.None); }
            await operation.Updates.CreateNodeAsync(["A", "B"], new Dictionary<string, object?>(), CancellationToken.None);
            before = database.Store.NodeRecordsDecoded;
            operation.Updates.GetNodes("A").Count.ShouldBe(3);
            (database.Store.NodeRecordsDecoded - before).ShouldBe(2);
            before = database.Store.NodeRecordsDecoded;
            operation.Updates.GetUnionNodes(["A", "B"], CancellationToken.None).Count.ShouldBe(5);
            (database.Store.NodeRecordsDecoded - before).ShouldBe(4);
            return true;
        }, CancellationToken.None);
    }

    private static async Task<List<object?[]>> RowsAsync(GraphDatabaseSession session, string source)
    {
        await using var result = (await session.ExecuteAsync(source, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
