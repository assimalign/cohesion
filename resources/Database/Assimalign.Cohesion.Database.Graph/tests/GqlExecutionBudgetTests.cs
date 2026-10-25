using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlExecutionBudgetTests
{
    private readonly ITestOutputHelper _output;

    public GqlExecutionBudgetTests(ITestOutputHelper output) { _output = output; }
    [Theory(DisplayName = "Cohesion Test [Graph] - Defaults preserve 100,000 one-property rows and refuse ten times it (w11 review)")]
    [InlineData("set")]
    [InlineData("insert")]
    [InlineData("return")]
    [InlineData("paths")]
    public async Task ExecuteAsync_DefaultEnvelope_ShouldAdmitOneHundredThousandRowsAndRefuseTenTimes(string shape)
    {
        // Arrange: small seed statements test each shape independently on default options.
        await using var engine = GraphDatabaseEngine.Create("graph-envelope", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?>
        { ["xs"] = Enumerable.Range(0, 1000).ToArray(), ["ys"] = Enumerable.Range(0, 100).ToArray() };
        if (shape != "insert")
        {
            for (int batch = 0; batch < 20; batch++)
            {
                await session.ExecuteAsync("FOR x IN $xs INSERT (:N {k:x})",
                    new Dictionary<string, object?> { ["xs"] = Enumerable.Range(batch * 5000, 5000).ToArray() }, CancellationToken.None);
            }
        }
        string source = shape switch
        {
            "set" => "MATCH (n:N) SET n.k=-1",
            "insert" => "FOR x IN $xs FOR y IN $ys INSERT (:N {k:x})",
            _ => "MATCH (n:N) RETURN n",
        };

        // Act / Assert: consume every result with no lower request limit.
        await VerifyAsync(source, 100_000);
        _output.WriteLine($"{shape}: 100,000 rows; peak={session.LastStatementMetrics!.Memory.PeakRetainedBytes} bytes");
        session.LastStatementMetrics!.Memory.PeakRetainedBytes.ShouldBeLessThanOrEqualTo(new GraphDatabaseEngineOptions().QueryMemoryBudget);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        parameters["ys"] = Enumerable.Range(0, 1000).ToArray();
        parameters["copies"] = Enumerable.Range(0, 10).ToArray();
        string excessive = shape switch
        {
            "set" => "MATCH (n:N) FOR copy IN $copies SET n.k=2",
            "insert" => source,
            _ => "MATCH (n:N) FOR copy IN $copies RETURN n",
        };
        var error = await Should.ThrowAsync<DatabaseException>(() => VerifyAsync(excessive, 1_000_000));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05020");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        await using var reused = (DatabaseResultSet)await session.ExecuteAsync("RETURN 7", cancellationToken: CancellationToken.None);

        async Task VerifyAsync(string query, int expected)
        {
            if (shape == "paths")
            {
                var paths = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(query, parameters), CancellationToken.None);
                paths.Paths.Count.ShouldBe(expected);
                paths.Paths[0].Nodes.ShouldHaveSingleItem().Properties.Count.ShouldBe(1);
                return;
            }
            var result = await session.ExecuteAsync(query, parameters, CancellationToken.None);
            if (result is DatabaseResultSet rows)
            {
                await using (rows)
                {
                    int count = 0;
                    await foreach (var row in rows.GetRowsAsync(CancellationToken.None))
                    { row.GetValue(0).ShouldBeOfType<GraphNode>().Properties.Count.ShouldBe(1); count++; }
                    count.ShouldBe(expected);
                }
            }
            else { result.AffectedCount.ShouldBe(expected); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Top N keeps source order for ties across the page boundary (w11 review)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_TopPageBoundaryTies_ShouldKeepEarliestSourceRows(bool boundary)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("graph-tied-page", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string page = " ORDER BY CASE WHEN x=0 THEN 9 ELSE 0 END OFFSET 2 LIMIT 3";
        string query = "FOR x IN $xs " + (boundary ? "WITH x" + page + " RETURN x" : "RETURN x" + page);

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query,
            new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 9).ToArray() }, CancellationToken.None);
        var actual = new List<long>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { actual.Add(Convert.ToInt64(row.GetValue(0))); }

        // Assert
        actual.ShouldBe(new long[] { 3, 4, 5 });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Refused deep expansion releases every ancestor cursor's target grant (#1533)")]
    public async Task ExecuteAsync_DeepPathWorkRefusal_ShouldReleaseAncestorTargetGrants()
    {
        await using var engine = GraphDatabaseEngine.Create("graph-deep-budget", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT " + string.Concat(Enumerable.Range(0, 8).Select(i =>
            (i == 0 ? "(:N {k:0})" : "") + "-[:T]->(:N {k:" + (i + 1) + "})")), cancellationToken: CancellationToken.None);
        var request = GraphQueryRequest.FromGql("MATCH (a:N {k:0})-[:T]->+(b) RETURN b.k ORDER BY b.k", null, new() { WorkUnits = 6 });
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBufferCapacity.ShouldBe(0);
        await using var reused = (DatabaseResultSet)await session.ExecuteAsync("RETURN 7", cancellationToken: CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Owned projection and list values remain budgeted while downstream rows stream (#1533)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_SuspendedOwnedValue_ShouldConsumeMemoryGrant(bool list)
    {
        await using var engine = GraphDatabaseEngine.Create("graph-suspended-budget", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string query = list ? "FOR x IN [" + string.Join(',', Enumerable.Range(0, 300)) + "] RETURN count(*)"
            : "WITH $payload AS p FOR x IN $xs RETURN count(*)";
        var request = GraphQueryRequest.FromGql(query, new Dictionary<string, object?>
        { ["payload"] = new string('p', 10000), ["xs"] = new[] { 1, 2 } }, new() { MemoryBytes = 8192 });
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05020");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBufferCapacity.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Deleting bindings releases their original value ownership (#1533)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DeleteBindings_ShouldReleaseEveryRetainedValue(bool relationship)
    {
        await using var engine = GraphDatabaseEngine.Create("graph-delete-budget", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(relationship ? "INSERT (:N)-[:T]->(:N)" : "INSERT (:N)", cancellationToken: CancellationToken.None);
        var result = await session.ExecuteAsync(relationship ? "MATCH ()-[r:T]->() DELETE r" : "MATCH (n:N) DELETE n", cancellationToken: CancellationToken.None);
        result.AffectedCount.ShouldBe(1);
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Traversal charges selected physical work and releases budgets on refusal (#1533)")]
    public async Task TraverseAsync_FilteredWork_ShouldRespectLowerLimitAndReleaseMemory()
    {
        await using var engine = GraphDatabaseEngine.Create("graph-traversal-budget", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var start = await database.CreateNodeAsync(session, ["N"], cancellationToken: CancellationToken.None);
        var target = await database.CreateNodeAsync(session, ["N"], cancellationToken: CancellationToken.None);
        await database.CreateRelationshipAsync(session, start.Id, target.Id, "T", cancellationToken: CancellationToken.None);
        for (int i = 0; i < 20; i++)
        { await database.CreateRelationshipAsync(session, start.Id, target.Id, "Other", cancellationToken: CancellationToken.None); }
        var traversal = new GraphTraversal(start.Id, RelationshipType: "T") { Limits = new() { WorkUnits = 2 } };
        var nodes = new List<GraphNode>();
        await foreach (var node in database.TraverseAsync(session, traversal, CancellationToken.None)) { nodes.Add(node); }
        nodes.ShouldHaveSingleItem().Id.ShouldBe(target.Id);
        session.LastStatementMetrics!.Physical.ShouldBe(2);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        { await foreach (var _ in database.TraverseAsync(session, traversal with { RelationshipType = null }, CancellationToken.None)) { } });
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05004");
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Query limits cannot exceed engine ceilings or disable enforcement (#1533)")]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    [InlineData(65537, 1)]
    [InlineData(1, 101)]
    public async Task ExecuteAsync_InvalidLimits_ShouldReturnOneCodedDiagnostic(long memory, long work)
    {
        await using var engine = GraphDatabaseEngine.Create("graph-invalid-limits", new() { QueryMemoryBudget = 65536, QueryWorkBudget = 100 });
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            GraphQueryRequest.FromGql("RETURN 1", null, new() { MemoryBytes = memory, WorkUnits = work }), CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG08021");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Canceled anchor iteration releases every cursor and retained-memory lease (#1533)")]
    public async Task ExecuteAsync_CanceledAnchor_ShouldReleaseMemoryAndReuseSession()
    {
        await using var engine = GraphDatabaseEngine.Create("graph-cancel-budget", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("FOR x IN $xs INSERT (:N {x:x})", new Dictionary<string, object?>
        { ["xs"] = Enumerable.Range(0, 30).ToArray() }, CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        int visits = 0;
        database.Store.OnLabelEntryRead = () => { if (++visits == 10) { canceled.Cancel(); } };
        try
        {
            var error = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
                "MATCH (a:N),(b:N) RETURN a.x,b.x ORDER BY a.x LIMIT 1", cancellationToken: canceled.Token));
            error.CancellationToken.ShouldBe(canceled.Token);
        }
        finally { database.Store.OnLabelEntryRead = null; }
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        await using var reused = (DatabaseResultSet)await session.ExecuteAsync("RETURN 7", cancellationToken: CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - A write-overlay budget refusal rolls back the statement and releases retained state (#1533)")]
    public async Task ExecuteAsync_WriteBudget_ShouldRollbackAndReleaseMemory()
    {
        await using var engine = GraphDatabaseEngine.Create("graph-write-budget", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var request = GraphQueryRequest.FromGql("FOR x IN $xs INSERT (:N {x:x})", new Dictionary<string, object?>
        { ["xs"] = Enumerable.Range(0, 50).ToArray() }, new() { MemoryBytes = 16 * 1024 });
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05020");
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        session.LastStatementMetrics.Memory.RetainedBytes.ShouldBe(0);
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("MATCH (n:N) RETURN count(*)", cancellationToken: CancellationToken.None);
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { Convert.ToInt64(row.GetValue(0)).ShouldBe(0); }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Ordered LIMIT retains a bounded top set at final and WITH boundaries (#1533)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_OrderedFanOut_ShouldRetainOnlyTopRows(bool boundary)
    {
        await using var engine = GraphDatabaseEngine.Create("graph-top-budget", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string query = "FOR x IN $xs FOR y IN $xs " + (boundary
            ? "WITH x,y ORDER BY x DESC,y DESC OFFSET 2 LIMIT 2 RETURN x,y"
            : "RETURN x,y ORDER BY x DESC,y DESC OFFSET 2 LIMIT 2");
        var request = GraphQueryRequest.FromGql(query, new Dictionary<string, object?>
        { ["xs"] = Enumerable.Range(0, 317).ToArray() }, new GraphQueryLimits { MemoryBytes = 16 * 1024 });
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
        var actual = new List<(long, long)>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { actual.Add((Convert.ToInt64(row.GetValue(0)), Convert.ToInt64(row.GetValue(1)))); }
        actual.ShouldBe(new[] { (316L, 314L), (316L, 313L) });
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Group and distinct aggregate state consume the query memory budget (#1533)")]
    [InlineData("FOR x IN $xs RETURN count(DISTINCT x)")]
    [InlineData("FOR x IN $xs WITH x,count(*) AS n RETURN count(*)")]
    [InlineData("FOR x IN $xs WITH collect_list(x) AS xs RETURN count(*)")]
    public async Task ExecuteAsync_RetainedAggregateState_ShouldRefuseAtMemoryBudget(string query)
    {
        await using var engine = GraphDatabaseEngine.Create("graph-aggregate-budget", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var request = GraphQueryRequest.FromGql(query, new Dictionary<string, object?>
        { ["xs"] = Enumerable.Range(0, 1000).ToArray() }, new GraphQueryLimits { MemoryBytes = 8192 });
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05020");
        session.LastStatementMetrics!.Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Streaming bindings: LIMIT and aggregate avoid retaining the complete fan-out (#1533)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_BoundedFanOut_ShouldStreamIntoLimitOrAggregate(bool aggregate)
    {
        await using var engine = GraphDatabaseEngine.Create("graph-execution-budget", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string query = "FOR x IN $xs FOR y IN $xs " + (aggregate ? "RETURN count(*) AS n" : "RETURN x,y LIMIT 1");
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query,
            new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 317).ToArray() }, CancellationToken.None);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            count++;
            Convert.ToInt64(row.GetValue(0)).ShouldBe(aggregate ? 100489 : 0);
            if (!aggregate) { Convert.ToInt64(row.GetValue(1)).ShouldBe(0); }
        }
        count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Query limits refuse explosive work or retained memory with one coded error (#1533)")]
    [InlineData(false, "CG05004")]
    [InlineData(true, "CG05020")]
    public async Task ExecuteAsync_LowerQueryLimit_ShouldRefuseAndLeaveSessionReusable(bool memory, string code)
    {
        await using var engine = GraphDatabaseEngine.Create("graph-lower-limits", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var request = GraphQueryRequest.FromGql("FOR x IN $xs RETURN x", new Dictionary<string, object?>
        { ["xs"] = Enumerable.Range(0, 100).ToArray() }, new GraphQueryLimits
        { MemoryBytes = memory ? 1024 : null, WorkUnits = memory ? null : 3 });
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        error.Message.ShouldNotContain(code, Case.Sensitive);
        session.LastStatementMetrics.ShouldNotBeNull().Memory.RetainedBytes.ShouldBe(0);
        session.LastStatementMetrics.Memory.ActiveLeases.ShouldBe(0);
        await using var reused = (DatabaseResultSet)await session.ExecuteAsync("RETURN 7", cancellationToken: CancellationToken.None);
        int rows = 0;
        await foreach (var row in reused.GetRowsAsync(CancellationToken.None))
        { Convert.ToInt64(row.GetValue(0)).ShouldBe(7); rows++; }
        rows.ShouldBe(1);
    }
}
