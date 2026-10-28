using System;
using System.Collections.Generic;
using System.IO;
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
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[Collection(nameof(GraphConcurrencyCollection))]
public sealed class GraphExistsLifetimeTests
{
    private readonly ITestOutputHelper _output;
    public GraphExistsLifetimeTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Graph] - EXISTS: cold and warm corpus has zero suspended inner evaluations")]
    public async Task ExecuteAsync_ColdAndWarmCorpus_ShouldCompleteEveryInnerEvaluationSynchronouslyAsync()
    {
        // Arrange: reopen before each case so its first read uses a fresh store and page pool.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-exists-sync-" + Guid.NewGuid().ToString("N"));
        long evaluations = 0;
        int cold = 0, warm = 0;
        try
        {
            foreach (var item in GraphExistsTests.PatternCases)
            {
                await using (var seedEngine = GraphDatabaseEngine.Create("exists-sync", new() { RootPath = root }))
                {
                    var seedDatabase = await seedEngine.CreateDatabaseAsync("graph", CancellationToken.None);
                    await using var seedSession = await seedDatabase.CreateSessionAsync(CancellationToken.None);
                    await seedSession.ExecuteAsync(GraphExistsTests.Seed, cancellationToken: CancellationToken.None);
                }
                await using (var engine = GraphDatabaseEngine.Create("exists-sync", new() { RootPath = root }))
                {
                    var database = await engine.OpenDatabaseAsync("graph", CancellationToken.None);
                    await using var session = await database.CreateSessionAsync(CancellationToken.None);
                    var statement = GraphQueryRequest.FromGql((string)item[0]).Statement;
                    for (int pass = 0; pass < 2; pass++)
                    {
                        // Act: exercise the actual scalar join throughout each statement.
                        await database.RunAsync(session, async operation =>
                        {
                            await using var result = (DatabaseResultSet)await GraphPlanExecutor.ExecuteAsync(database, operation, statement, null, CancellationToken.None);
                            var rows = new List<object?[]>();
                            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
                            { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
                            // Assert: prove the seam ran, check results, and reject every suspension.
                            var registry = operation.FunctionBindings.ShouldBeOfType<GraphCorrelatedBindings>();
                            registry.InnerEvaluations.ShouldBeGreaterThan(0);
                            registry.SuspendedInnerEvaluations.ShouldBe(0, (string)item[0]);
                            evaluations += registry.InnerEvaluations;
                            string.Join(';', rows.Select(row => string.Join(',', row.Select(GraphExistsTests.Text)))).ShouldBe((string)item[1]);
                            return true;
                        }, CancellationToken.None);
                        if (pass == 0) { cold++; } else { warm++; }
                    }
                }
                await using var cleanup = GraphDatabaseEngine.Create("exists-sync", new() { RootPath = root });
                await cleanup.DropDatabaseAsync("graph", CancellationToken.None);
            }
            cold.ShouldBe(GraphExistsTests.PatternCases.Count());
            warm.ShouldBe(cold);
            _output.WriteLine($"EXISTS synchronous seam: cold statements={cold}; warm statements={warm}; inner evaluations={evaluations}; suspended=0.");
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - EXISTS: the tripwire distinguishes suspension from synchronous faults")]
    public void ObserveInnerEvaluation_IncompleteTask_ShouldCountSuspensionOnly()
    {
        // Arrange
        var registry = new GraphCorrelatedBindings(new Dictionary<GqlNamedFunctionCallExpression, GraphFunction>(),
            new Dictionary<GqlExpression, GraphCorrelatedSubplan>(), null);
        var pending = new TaskCompletionSource<bool>();
        // Act
        registry.ObserveInnerEvaluation(ValueTask.FromResult(true).IsCompleted);
        registry.ObserveInnerEvaluation(ValueTask.FromException<bool>(new InvalidOperationException()).IsCompleted);
        registry.ObserveInnerEvaluation(pending.Task.IsCompleted);
        pending.SetResult(true);
        // Assert
        registry.InnerEvaluations.ShouldBe(3);
        registry.SuspendedInnerEvaluations.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - EXISTS: registered calls preserve ordinary projection and ordering evaluation")]
    public async Task ExecuteAsync_RegisteredInnerPredicate_ShouldEvaluateEachExpressionAsync()
    {
        // Arrange
        int calls = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("exists-function-reuse");
        builder.Functions.Add(GraphScalarFunction.Create<long, bool>("observe", value => { calls++; return value > 0; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1})", cancellationToken: CancellationToken.None);
        // Act
        var rows = await GraphExistsTests.RowsAsync(session, "RETURN EXISTS { (n:N) WHERE observe(n.k) } AS yes ORDER BY EXISTS { (n:N) WHERE observe(n.k) }");
        // Assert
        rows.Single()[0].ShouldBe(true);
        calls.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - NOT EXISTS: first matching edge ends a correlated fanout without changing arguments")]
    public async Task Evaluate_CorrelatedFanout_ShouldReadFirstEdgeAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("exists-edge-first", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var start = await database.CreateNodeAsync(session, ["Start"], cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("MATCH (a:Start) FOR x IN [" + string.Join(',', Enumerable.Range(0, 200)) + "] INSERT (a)-[:T]->(:End)", cancellationToken: CancellationToken.None);
        // Act / Assert
        await database.RunAsync(session, operation =>
        {
            var query = GraphQueryRequest.FromGql("MATCH (a:Start) RETURN NOT EXISTS { (a)-[:T]->(b) }").Statement.GqlExpression;
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(query);
            operation.FunctionBindings = plan.FunctionBindings;
            var registry = plan.FunctionBindings.ShouldBeOfType<GraphCorrelatedBindings>();
            int advances = 0, disposed = 0;
            registry.OnInnerAdvance = () => advances++;
            registry.OnInnerDisposed = () => disposed++;
            var arguments = new Dictionary<string, object?> { ["a"] = start };
            object? original = arguments["a"];
            var budget = operation.QueryBudget = new GraphPlanExecutor.ExpansionBudget();
            GraphExpressionEvaluator.Evaluate(query.Projections[0].Expression, arguments, operation.EvaluationContext).ShouldBe(false);
            advances.ShouldBe(1);
            disposed.ShouldBe(1);
            registry.InnerEvaluations.ShouldBe(1);
            registry.SuspendedInnerEvaluations.ShouldBe(0);
            budget.Physical.ShouldBe(2);
            budget.Viable.ShouldBe(1);
            arguments.Keys.ShouldBe(new[] { "a" });
            arguments["a"].ShouldBeSameAs(original);
            budget.Memory.RetainedBytes.ShouldBe(0);
            budget.Memory.ActiveLeases.ShouldBe(0);
            _output.WriteLine($"Correlated anti fanout: available=200; advances={advances}; physical={budget.Physical}; viable={budget.Viable}; disposed={disposed}.");
            return ValueTask.FromResult(true);
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - EXISTS: semi and anti consumers advance once and release inner iterators")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaluate_ManyInnerMatches_ShouldAdvanceOncePerOuterRowAsync(bool negated)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("exists-first", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("FOR x IN [" + string.Join(',', Enumerable.Range(0, 200)) + "] INSERT (:N {k:x})", cancellationToken: CancellationToken.None);
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var query = GraphQueryRequest.FromGql("RETURN " + (negated ? "NOT " : "") + "EXISTS { (n:N) }").Statement.GqlExpression;
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(query);
            operation.FunctionBindings = plan.FunctionBindings;
            var registry = plan.FunctionBindings.ShouldBeOfType<GraphCorrelatedBindings>();
            int advances = 0, disposed = 0;
            registry.OnInnerAdvance = () => advances++;
            registry.OnInnerDisposed = () => disposed++;
            var arguments = new Dictionary<string, object?>();
            var budget = operation.QueryBudget = new GraphPlanExecutor.ExpansionBudget();
            for (int i = 0; i < 40; i++)
            {
                GraphExpressionEvaluator.Evaluate(query.Projections[0].Expression, arguments, operation.EvaluationContext).ShouldBe(!negated);
                budget.Memory.RetainedBytes.ShouldBe(0);
                budget.Memory.ActiveLeases.ShouldBe(0);
            }
            advances.ShouldBe(40);
            disposed.ShouldBe(40);
            registry.InnerEvaluations.ShouldBe(40);
            registry.SuspendedInnerEvaluations.ShouldBe(0);
            budget.Physical.ShouldBe(40);
            _output.WriteLine($"{(negated ? "AntiSemiApply" : "SemiApply")}: 40 outer rows, 200 available inner rows each; advances={advances}; physical={budget.Physical}; disposed={disposed}.");
            // The same boundary can be drained by GQ-C3 without a second correlation mechanism.
            int collected = 0;
            await foreach (var _ in GraphPlanExecutor.ExecuteCorrelatedAsync(database, operation, registry.Subplans.Values.Single(), arguments, null, CancellationToken.None))
            { collected++; }
            collected.ShouldBe(200);
            budget.Memory.RetainedBytes.ShouldBe(0);
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - EXISTS: inner scan cancellation, budget and value failures release every lease")]
    [InlineData("cancel")]
    [InlineData("work")]
    [InlineData("value")]
    [InlineData("memory")]
    public async Task Evaluate_InnerFailure_ShouldDisposeAndReleaseAsync(string failure)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var engine = GraphDatabaseEngine.Create("exists-failure", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("FOR x IN [1,2,3,4,5,6] INSERT (:N {k:x})", cancellationToken: CancellationToken.None);
        // Act / Assert
        await database.RunAsync(session, operation =>
        {
            var query = GraphQueryRequest.FromGql("RETURN EXISTS { (n:N) WHERE " + (failure == "value" ? "1/0 = 1" : "n.k < 0") + " }").Statement.GqlExpression;
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(query);
            operation.FunctionBindings = plan.FunctionBindings;
            operation.FunctionCancellation = cancellation.Token;
            var registry = plan.FunctionBindings.ShouldBeOfType<GraphCorrelatedBindings>();
            int disposed = 0;
            registry.OnInnerDisposed = () => disposed++;
            var budget = operation.QueryBudget = new GraphPlanExecutor.ExpansionBudget(failure == "work" ? 2 : 100, failure == "memory" ? 512 : 1_000_000)
            { OnConsume = count => { if (failure == "cancel" && count == 3) { cancellation.Cancel(); } } };
            Action evaluate = () => GraphExpressionEvaluator.Evaluate(query.Projections[0].Expression, new Dictionary<string, object?>(), operation.EvaluationContext);
            if (failure == "cancel") { Should.Throw<OperationCanceledException>(evaluate); budget.Physical.ShouldBe(3); }
            else { Should.Throw<DatabaseException>(evaluate).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(failure == "work" ? "CG05004" : failure == "memory" ? "CG05020" : "CG02013"); }
            disposed.ShouldBe(1);
            registry.InnerEvaluations.ShouldBe(1);
            registry.SuspendedInnerEvaluations.ShouldBe(0);
            budget.Memory.RetainedBytes.ShouldBe(0);
            budget.Memory.ActiveLeases.ShouldBe(0);
            return ValueTask.FromResult(true);
        }, CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - EXISTS: malformed bodies and incompatible runtime arguments carry dedicated diagnostics")]
    public async Task Plan_InvalidSubplan_ShouldReportDedicatedIdentityAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("exists-contract", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        await database.RunAsync(session, async operation =>
        {
            var malformed = new GqlQueryExpression([], null, [], [], false, [new GqlProjection(new GqlExistsExpression([]))]);
            Should.Throw<DatabaseException>(() => new GraphPlanner(database, operation.Context.Snapshot).Plan(malformed))
                .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05027");
            var query = GraphQueryRequest.FromGql("MATCH (a) RETURN EXISTS { (a) }").Statement.GqlExpression;
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(query);
            operation.FunctionBindings = plan.FunctionBindings;
            var subplan = ((GraphCorrelatedBindings)plan.FunctionBindings).Subplans.Values.Single();
            var invalid = await Should.ThrowAsync<DatabaseException>(async () => await GraphPlanExecutor.ExistsAsync(database, operation,
                subplan, new Dictionary<string, object?> { ["a"] = 1L }, null, CancellationToken.None));
            invalid.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02016");
            var missing = await Should.ThrowAsync<DatabaseException>(async () => await GraphPlanExecutor.ExistsAsync(database, operation,
                subplan, new Dictionary<string, object?>(), null, CancellationToken.None));
            missing.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05027");
            return true;
        }, CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - EXISTS: inner reads share the statement snapshot and transaction writes")]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.Snapshot)]
    public async Task Evaluate_ConcurrentCommit_ShouldRetainStatementSnapshotAsync(IsolationLevel isolation)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("exists-snapshot", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("INSERT (:N {k:0})", cancellationToken: CancellationToken.None);
        await using var transaction = await reader.BeginTransactionAsync(isolation, CancellationToken.None);
        var operation = await database.BeginOperationAsync(reader, CancellationToken.None);
        try
        {
            var query = GraphQueryRequest.FromGql("RETURN EXISTS { (:N {k:1}) }").Statement.GqlExpression;
            var plan = new GraphPlanner(database, operation.Context.Snapshot).Plan(query);
            operation.FunctionBindings = plan.FunctionBindings;
            // Act: commit after this statement's snapshot, while its pin remains live.
            await writer.ExecuteAsync("INSERT (:N {k:1})", cancellationToken: CancellationToken.None);
            GraphExpressionEvaluator.Evaluate(query.Projections[0].Expression, new Dictionary<string, object?>(), operation.EvaluationContext).ShouldBe(false);
            await operation.CompleteAsync();
        }
        catch (Exception error) { await operation.AbortAsync(error); throw; }
        await transaction.CommitAsync(CancellationToken.None);
        // Assert: a later statement and the transaction's own writes are visible normally.
        (await GraphExistsTests.RowsAsync(reader, "RETURN EXISTS { (:N {k:1}) }")).Single()[0].ShouldBe(true);
        await using var own = await reader.BeginTransactionAsync(isolation, CancellationToken.None);
        await reader.ExecuteAsync("INSERT (:N {k:2})", cancellationToken: CancellationToken.None);
        (await GraphExistsTests.RowsAsync(reader, "RETURN EXISTS { (:N {k:2}) }")).Single()[0].ShouldBe(true);
        (await GraphExistsTests.RowsAsync(reader, "INSERT (:Fresh {k:3}) RETURN EXISTS { (:Fresh {k:3}) }")).Single()[0].ShouldBe(true);
        await own.RollbackAsync(CancellationToken.None);
    }
}
