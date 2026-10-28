using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphReducePlanningTests
{
    private readonly ITestOutputHelper _output;
    public GraphReducePlanningTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - Reduce static kinds refuse numeric predicates and sources even on empty input")]
    [InlineData("all(x IN [] WHERE reduce(a=1,y IN [] | a))", "CG02013")]
    [InlineData("any(x IN [] WHERE reduce(a=1,y IN [] | a))", "CG02013")]
    [InlineData("none(x IN [] WHERE reduce(a=1,y IN [] | a))", "CG02013")]
    [InlineData("single(x IN [] WHERE reduce(a=1,y IN [] | a))", "CG02013")]
    [InlineData("all(x IN reduce(a=1,y IN [] | a) WHERE TRUE)", "CG02015")]
    [InlineData("any(x IN reduce(a=1,y IN [] | a) WHERE TRUE)", "CG02015")]
    [InlineData("none(x IN reduce(a=1,y IN [] | a) WHERE TRUE)", "CG02015")]
    [InlineData("single(x IN reduce(a=1,y IN [] | a) WHERE TRUE)", "CG02015")]
    [InlineData("reduce(b=0,x IN reduce(a=1,y IN [] | a) | b)", "CG02015")]
    [InlineData("all(x IN [] WHERE reduce(a=1,y IN [] | CASE WHEN TRUE THEN a ELSE 's' END))", "CG02013")]
    [InlineData("all(x IN [] WHERE reduce(a=1,y IN [] | CASE WHEN TRUE THEN a ELSE NULL END))", "CG02013")]
    [InlineData("all(x IN [] WHERE reduce(a=NULL,y IN [] | 1))", "CG02013")]
    [InlineData("all(x IN [] WHERE reduce(a=1,y IN [1] | reduce(b=a,z IN [] | b)))", "CG02013")]
    [InlineData("all(x IN [] WHERE reduce(a=1,y IN [1] | a+y))", "CG02013")]
    public async Task ExecuteAsync_NumericReduction_ShouldRefuseDuringPlanningAsync(string expression, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("reduce-static", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "EXPLAIN RETURN ", "RETURN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + expression, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Reduce shape planning stays below two MiB for empty and thousand-element wrapping folds")]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task Plan_SelfWrappingReduction_ShouldBoundAllocationAsync(int elements)
    {
        // Arrange: parse and snapshot outside the synchronous, same-thread measurement.
        await using var engine = GraphDatabaseEngine.Create("reduce-allocation", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var query = GraphQueryRequest.FromGql("RETURN reduce(a=[],x IN [" + string.Join(",", Enumerable.Repeat("1", elements)) + "] | [a,x])").Statement.GqlExpression;
        // Act
        long bytes = await database.RunAsync(session, operation =>
        {
            void Plan() => GC.KeepAlive(new GraphPlanner(database, operation.Context.Snapshot).Plan(query));
            Plan();
            long before = GC.GetAllocatedBytesForCurrentThread();
            Plan();
            return ValueTask.FromResult(GC.GetAllocatedBytesForCurrentThread() - before);
        }, CancellationToken.None);
        // Assert
        _output.WriteLine($"Self-wrapping source {elements}: {bytes:N0} planning bytes");
        bytes.ShouldBeLessThan(2L * 1024 * 1024);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Deep self-wrapping reduce is refused with bounded allocation")]
    public async Task ExecuteAsync_DeepWrappingReduction_ShouldRefuseCheaplyAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("reduce-deep", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var request = GraphQueryRequest.FromGql("RETURN reduce(a=[],x IN $xs | [a,x])", new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 1000).ToArray() });
        await session.ExecuteAsync("RETURN 1", cancellationToken: CancellationToken.None);
        // Act
        long before = GC.GetTotalAllocatedBytes(true);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
        long bytes = GC.GetTotalAllocatedBytes(true) - before;
        // Assert
        _output.WriteLine($"Deep wrapping refusal: {bytes:N0} bytes; {error.Diagnostics[0].Code}");
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
        bytes.ShouldBeLessThan(4L * 1024 * 1024);
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Scalar reduce planning retains a small constant allocation bound")]
    public async Task Plan_ScalarReduction_ShouldRetainBoundedPlanningAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("reduce-scalar-plan", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var query = GraphQueryRequest.FromGql("RETURN reduce(a=0,x IN [] | a+x)").Statement.GqlExpression;
        // Act
        long bytes = await database.RunAsync(session, operation =>
        {
            void Plan() => GC.KeepAlive(new GraphPlanner(database, operation.Context.Snapshot).Plan(query));
            Plan();
            long before = GC.GetAllocatedBytesForCurrentThread();
            Plan();
            return ValueTask.FromResult(GC.GetAllocatedBytesForCurrentThread() - before);
        }, CancellationToken.None);
        // Assert
        _output.WriteLine($"Scalar fold: {bytes:N0} planning bytes");
#if DEBUG
        bytes.ShouldBeLessThanOrEqualTo(27960L); // Original-head planner-only Debug allocation.
#else
        bytes.ShouldBeLessThanOrEqualTo(30648L); // Original-head Release scalar-fold comparison.
#endif
        await using var rows = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN RETURN reduce(a=0,x IN [] | a+x)", cancellationToken: CancellationToken.None);
    }
    [Theory(DisplayName = "Cohesion Test [Graph] - Reduce kind unions retain Boolean alternatives and unknown runtime validation")]
    [InlineData("reduce(a=1,y IN [1] | TRUE)", true)]
    [InlineData("reduce(a=TRUE,y IN [] | 1)", true)]
    [InlineData("reduce(a=NULL,y IN [] | a)", null)]
    [InlineData("reduce(a=1,y IN [] | n.k)", null)]
    public async Task ExecuteAsync_UnionContainingBooleanOrUnknown_ShouldRetainRuntimeChecksAsync(string expression, bool? expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("reduce-union", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1})", cancellationToken: CancellationToken.None);
        string query = $"MATCH (n:N) RETURN all(x IN [1] WHERE {expression})";
        // Act / Assert
        await using var plan = (DatabasePlanDescription)await session.ExecuteAsync("EXPLAIN " + query, cancellationToken: CancellationToken.None);
        if (expression.Contains("n.k", StringComparison.Ordinal))
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
        }
        else
        {
            await using var rows = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
            await foreach (var row in rows.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(expected); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Reduce shape closure observes cancellation after recorded scope progress")]
    public void Plan_CanceledShapeClosure_ShouldStopAtNextVisit()
    {
        // Arrange
        using var canceled = new CancellationTokenSource();
        var expression = (GqlReduceExpression)GraphQueryRequest.FromGql("RETURN reduce(a=x,y IN [] | [a,y])").Statement.GqlExpression.Projections[0].Expression;
        int visits = 0;
        var analysis = new GraphReductionAnalysis(new GraphReductionWork(canceled.Token));
        // Act
        Should.Throw<OperationCanceledException>(() => GraphValueExpressions.IsGroup(expression, (_, _) =>
        { visits++; canceled.Cancel(); return false; }, 0, analysis: analysis));
        // Assert
        visits.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Expensive reduce planning has a fixed coded cap independent of execution budgets")]
    public async Task ExecuteAsync_ExcessiveShapeAnalysis_ShouldRefuseBeforeExecutionAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("reduce-plan-cap", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string body = "a";
        for (int i = 0; i < 4; i++) { body = $"reduce(a=[],x IN [] | [{body},x])"; }
        string query = "EXPLAIN RETURN " + body;
        // Act
        long before = GC.GetTotalAllocatedBytes(true);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        long bytes = GC.GetTotalAllocatedBytes(true) - before;
        // Assert
        _output.WriteLine($"Planning cap: {bytes:N0} bytes; {error.Diagnostics[0].Code}");
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05026");
        bytes.ShouldBeLessThan(32L * 1024 * 1024);
    }
}
