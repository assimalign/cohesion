using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Graph.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphPatternComprehensionLifetimeTests
{
    private readonly ITestOutputHelper _output;
    public GraphPatternComprehensionLifetimeTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Graph] - Pattern collection: each element is visited once and all inner leases are released")]
    [InlineData(20)]
    [InlineData(200)]
    public async Task Evaluate_Fanout_ShouldDrainExactlyOnceAsync(int count)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("pattern-drain", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:Root) FOR x IN [" + string.Join(',', Enumerable.Range(0, count)) + "] INSERT (a)-[:T]->(:N {k:x})", cancellationToken: CancellationToken.None);
        // Act / Assert: exact progress counters prove linear collection, without a clock bound.
        await database.RunAsync(session, operation =>
        {
            var query = GraphQueryRequest.FromGql("RETURN [p=(:Root)-[:T]->(b) | b.k]").Statement.GqlExpression;
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(query);
            operation.FunctionBindings = plan.FunctionBindings;
            var registry = plan.FunctionBindings.ShouldBeOfType<GraphCorrelatedBindings>();
            int advances = 0, disposed = 0;
            registry.OnInnerAdvance = () => advances++;
            registry.OnInnerDisposed = () => disposed++;
            var budget = operation.QueryBudget = new GraphPlanExecutor.ExpansionBudget();
            var result = GraphExpressionEvaluator.Evaluate(query.Projections[0].Expression, new Dictionary<string, object?>(), operation.EvaluationContext).ShouldBeOfType<object?[]>();
            result.ShouldBe(Enumerable.Range(0, count).Select(x => (object?)(long)x));
            advances.ShouldBe(count + 1);
            disposed.ShouldBe(1);
            registry.SuspendedInnerEvaluations.ShouldBe(0);
            budget.Memory.RetainedBytes.ShouldBe(0);
            budget.Memory.ActiveLeases.ShouldBe(0);
            _output.WriteLine($"Collection fanout={count}; advances={advances}; physical={budget.Physical}; viable={budget.Viable}; disposed={disposed}; peak={budget.Memory.PeakRetainedBytes}.");
            return ValueTask.FromResult(true);
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Pattern collection: cancellation and refusals after progress dispose and release everything")]
    [InlineData("cancel")]
    [InlineData("work")]
    [InlineData("memory")]
    [InlineData("value")]
    public async Task Evaluate_MidCollectionFailure_ShouldDisposeAndReleaseAsync(string failure)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var engine = GraphDatabaseEngine.Create("pattern-failure", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:Root) FOR x IN [1,2,3,4,5,6] INSERT (a)-[:T]->(:N {k:x})", cancellationToken: CancellationToken.None);
        // Act / Assert
        await database.RunAsync(session, operation =>
        {
            var query = GraphQueryRequest.FromGql("RETURN [p=(:Root)->(b) | " + (failure == "value" ? "1/(b.k-3)" : failure == "memory" ? "[x IN $payload | x]" : "b.k") + "]").Statement.GqlExpression;
            var parameters = new Dictionary<string, object?> { ["payload"] = Enumerable.Range(0, 100).Select(x => (object?)(long)x).ToArray() };
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(query, parameters);
            operation.FunctionBindings = plan.FunctionBindings;
            operation.FunctionCancellation = cancellation.Token;
            var registry = plan.FunctionBindings.ShouldBeOfType<GraphCorrelatedBindings>();
            int advances = 0, disposed = 0;
            registry.OnInnerAdvance = () => { if (++advances == 4 && failure == "cancel") { cancellation.Cancel(); } };
            registry.OnInnerDisposed = () => disposed++;
            var budget = operation.QueryBudget = new GraphPlanExecutor.ExpansionBudget(failure == "work" ? 8 : 10000, 1000000);
            if (failure == "memory")
            {
                // Derive the refusal grant from this operation's complete retained peak.
                // The final collected array coexists with every projected element.
                var calibration = GraphPlanExecutor.CollectPatternAsync(database, operation, registry.Subplans.Values.Single(),
                    new Dictionary<string, object?>(), plan.Parameters, cancellation.Token);
                calibration.IsCompleted.ShouldBeTrue();
                calibration.GetAwaiter().GetResult().Length.ShouldBe(6);
                long limit = budget.Memory.PeakRetainedBytes - 1;
                budget.Memory.RetainedBytes.ShouldBe(0);
                budget = operation.QueryBudget = new GraphPlanExecutor.ExpansionBudget(10000, limit);
                advances = 0; disposed = 0;
            }
            Action evaluate = () => GraphExpressionEvaluator.Evaluate(query.Projections[0].Expression, new Dictionary<string, object?>(), operation.EvaluationContext, plan.Parameters);
            if (failure == "cancel") { Should.Throw<OperationCanceledException>(evaluate); advances.ShouldBe(4); }
            else { Should.Throw<DatabaseException>(evaluate).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(failure == "work" ? "CG05004" : failure == "memory" ? "CG05020" : "CG02013"); }
            advances.ShouldBeGreaterThan(1);
            disposed.ShouldBe(1);
            registry.InnerEvaluations.ShouldBe(1);
            registry.SuspendedInnerEvaluations.ShouldBe(0);
            budget.Memory.RetainedBytes.ShouldBe(0);
            budget.Memory.ActiveLeases.ShouldBe(0);
            return ValueTask.FromResult(true);
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Pattern collection: in-process nodes relationships and paths retain each match")]
    public async Task ExecuteAsync_EntityProjections_ShouldRetainTypedValuesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("pattern-entities", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(GraphExistsTests.Seed, cancellationToken: CancellationToken.None);
        // Act
        var row = (await GraphExistsTests.RowsAsync(session, "RETURN [(:N {k:1})->(b) | b], [()-[r:T]->() | r], [p=()-[:T]->() | p]")).Single();
        // Assert
        row[0].ShouldBeOfType<object?[]>().Select(x => x.ShouldBeOfType<GraphNode>().Properties["k"]).ShouldBe(new object?[] { 2L,2L });
        row[1].ShouldBeOfType<object?[]>().Length.ShouldBe(3);
        row[1].ShouldBeOfType<object?[]>().ShouldAllBe(x => x is GraphRelationship);
        row[2].ShouldBeOfType<object?[]>().Select(x => x.ShouldBeOfType<GraphPath>().Nodes.Count).ShouldBe(new[] { 2,2,2 });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Pattern collection: EXPLAIN exposes the collected inner plan and lexical arguments")]
    public async Task ExecuteAsync_Explain_ShouldDescribeCollectionAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("pattern-explain", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await GraphExistsTests.RowsAsync(session, "EXPLAIN MATCH (a) RETURN [(a)->(b) | [(b)->(c) | c.k]]");
        // Assert
        string text = string.Join(';', rows.SelectMany(row => row).Select(GraphExistsTests.Text));
        text.ShouldContain("CollectApply", Case.Sensitive);
        text.ShouldContain("correlated=a", Case.Sensitive);
        text.ShouldContain("correlated=b", Case.Sensitive);
        text.ShouldContain("first_match=False; materialize=True", Case.Sensitive);
        text.ShouldContain("CorrelatedSubplan", Case.Sensitive);
    }
}
