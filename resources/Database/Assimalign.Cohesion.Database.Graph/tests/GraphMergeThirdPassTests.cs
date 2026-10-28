using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphMergeThirdPassTests
{
    private readonly ITestOutputHelper _output;

    public GraphMergeThirdPassTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - overlay label scans ignore unrelated CREATE and MERGE labels (#1735)")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Execute_UnrelatedOverlayLabels_ShouldCompleteWithinLabelWork(bool merge, bool measure)
    {
        // Arrange: the larger limit records the complete regressed work, while the default
        // limit proves that a realistic import completes without increasing its budget.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("third", new() { QueryWorkBudget = measure ? 5_000_000 : 1_000_000 });
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await db.CreateSessionAsync(token);
        await session.ExecuteAsync("CREATE (:Category {name:'category'}), (:Tag {name:'tag'})", cancellationToken: token);
        var schema = GraphSchema.Open(db, session);
        await schema.SaveLabelAsync(new(Guid.NewGuid(), "Person"), token);
        await schema.CreateIndexAsync("Person", "by_id", "id", token);
        var rows = Enumerable.Range(0, 1100).Select(i => new Dictionary<string, object?>
        { ["id"] = (long)i, ["cat"] = "category", ["pid"] = (long)i, ["tag"] = "tag" }).ToArray();
        string source = merge
            ? "UNWIND $rows AS r MERGE (p:Person {id:r.pid}) MERGE (t:Tag {name:r.tag}) RETURN count(*)"
            : "UNWIND $rows AS r CREATE (:Item {id:r.id}) WITH r MATCH (c:Category {name:r.cat}) RETURN count(*)";

        // Act
        try
        {
            var result = await session.ExecuteAsync(source, new Dictionary<string, object?> { ["rows"] = rows }, token);

            // Assert
            (await GraphMergeExecutionTests.Read(result)).Single()[0].ShouldBe(1100L);
            result.AffectedCount.ShouldBe(1100);
            session.LastStatementMetrics!.Consumed.ShouldBeLessThan(50_000);
        }
        finally { _output.WriteLine($"label import: merge={merge}; measure={measure}; work={session.LastStatementMetrics?.Consumed}; peak={session.LastStatementMetrics?.Memory.PeakRetainedBytes}"); }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - overlay label unions deduplicate selected postings and node scans visit all nodes (#1735)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamAnchorAsync_LabelSelection_ShouldChargeOnlySelectedPostings(bool union)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("third", new());
        var db = await engine.CreateDatabaseAsync("patterns", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await db.RunAsync(session, async operation =>
        {
            await operation.Updates.CreateNodeAsync(["A", "B"], new Dictionary<string, object?>(), CancellationToken.None);
            await operation.Updates.CreateNodeAsync(["B"], new Dictionary<string, object?>(), CancellationToken.None);
            await operation.Updates.CreateNodeAsync(["Other"], new Dictionary<string, object?>(), CancellationToken.None);
            var budget = new GraphPlanExecutor.ExpansionBudget();
            var anchor = union ? new GraphAnchor(0, null, null, null) { UnionLabels = ["A", "B"] } : new GraphAnchor(0, null, null, null);

            // Act
            int count = 0;
            await foreach (var node in operation.Updates.StreamAnchorAsync(anchor, budget, CancellationToken.None)) { count++; }

            // Assert
            count.ShouldBe(union ? 2 : 3);
            budget.Consumed.ShouldBe(union ? 2 : 3);
            return true;
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - persisted ON MATCH SET avoids redundant original-value postings (#1735)")]
    public async Task Execute_PersistedUpsert_ShouldRetainOnlyCurrentPostings()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        await using var engine = GraphDatabaseEngine.Create("third", new() { QueryWorkBudget = 5_000_000, QueryMemoryBudget = 512L * 1024 * 1024 });
        var db = await engine.CreateDatabaseAsync("patterns", token);
        await using var session = await db.CreateSessionAsync(token);
        var parameters = new Dictionary<string, object?>
        {
            ["batches"] = Enumerable.Range(0, 1000).Select(i => (object?)(long)i).ToArray(),
            ["offsets"] = Enumerable.Range(0, 100).Select(i => (object?)(long)i).ToArray(),
        };
        await session.ExecuteAsync("UNWIND $batches AS b UNWIND $offsets AS i CREATE (:Person {id:b*100+i})", parameters, token);
        var schema = GraphSchema.Open(db, session);
        await schema.CreateIndexAsync("Person", "by_id", "id", token);

        // Act
        var result = await session.ExecuteAsync("UNWIND $batches AS b UNWIND $offsets AS i MERGE (n:Person {id:b*100+i}) ON MATCH SET n.hit=1 RETURN count(*)", parameters, token);
        var metrics = session.LastStatementMetrics!;

        // Assert: 100000 obsolete postings would add 100000 units and 41600000 bytes.
        _output.WriteLine($"persisted upsert: rows=100000; work={metrics.Consumed}; peak={metrics.Memory.PeakRetainedBytes}; retained={metrics.Memory.RetainedBytes}; leases={metrics.Memory.ActiveLeases}");
        result.AffectedCount.ShouldBe(100_000);
        (await GraphMergeExecutionTests.Read(result)).Single()[0].ShouldBe(100_000L);
        metrics.Consumed.ShouldBeLessThan(610_000);
        metrics.Memory.PeakRetainedBytes.ShouldBeLessThanOrEqualTo(268_435_456);
        metrics.Memory.ActiveLeases.ShouldBe(0);
        metrics.Memory.RetainedBytes.ShouldBe(0);
    }

}
