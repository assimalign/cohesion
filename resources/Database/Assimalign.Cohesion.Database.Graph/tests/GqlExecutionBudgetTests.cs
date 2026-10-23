using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GqlExecutionBudgetTests
{
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
