using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

[CollectionDefinition("CALL stack guards", DisableParallelization = true)]
public sealed class GqlCallStackCollection { }

[Collection("CALL stack guards")]
public sealed class GqlCallStackTests
{
    private readonly ITestOutputHelper _output;

    public GqlCallStackTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Graph] - CALL parse exhaustion reports CG05008 and retains the session (#1807)")]
    public async Task Execute_CallParsePastStack_ShouldRetainSession()
    {
        // Arrange
        const int depth = 10000;
        string source = string.Concat(Enumerable.Repeat("CALL () { ", depth)) + "RETURN 1 AS y" +
            string.Concat(Enumerable.Repeat(" } RETURN y", depth));
        await using var engine = GraphDatabaseEngine.Create("call-parse-stack", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        int stoppedDepth = -1;
        // Act
        var error = OnThread(() =>
        {
            var parsed = (GqlQueryStatement)new GqlQueryParser().Parse(source);
            stoppedDepth = parsed.Diagnostics.First().Start!.Value / 10;
            return session.ExecuteAsync(new GraphQueryRequest(parsed), CancellationToken.None).AsTask();
        }, 512 * 1024);
        // Assert
        var failure = error.ShouldBeOfType<DatabaseException>();
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05008");
        failure.InnerException.ShouldBeOfType<InsufficientExecutionStackException>();
        _output.WriteLine($"CALL parse: requested depth={depth}; stopped at depth={stoppedDepth}; stack=512 KiB");
        await ReuseAsync(session);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - CALL planning exhaustion reports CG05008 and retains the session (#1807)")]
    public async Task Execute_CallPlanPastStack_ShouldRetainSession()
    {
        // Arrange: construct the nested AST iteratively so parsing cannot be the failing layer.
        const int depth = 1000;
        var template = GraphQueryRequest.FromGql("MATCH (n) LET marker=n.k CALL () { RETURN 1 AS y } RETURN y").Statement.GqlExpression;
        var stages = template.Stages;
        var query = GraphQueryRequest.FromGql("RETURN 1 AS y").Statement.GqlExpression;
        for (int i = 0; i < depth; i++)
        { query = new GqlQueryExpression(stages.Select(stage => stage is GqlCallStage call ? call with { Body = query } : stage).ToArray()); }
        await using var engine = GraphDatabaseEngine.Create("call-plan-stack", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        int checks = 0;
        database.Catalog.BeforePropertyCheck = () => checks++;
        // Act
        Exception? error;
        try
        {
            error = OnThread(() => database.RunAsync(session, operation =>
                ValueTask.FromResult(new GraphPlanner(database, operation.Context.Snapshot).Plan(query)), CancellationToken.None).AsTask(), 512 * 1024);
        }
        finally { database.Catalog.BeforePropertyCheck = null; }
        // Assert
        var failure = error.ShouldBeOfType<DatabaseException>();
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05008");
        failure.InnerException.ShouldBeOfType<InsufficientExecutionStackException>();
        checks.ShouldBeInRange(1, depth - 1);
        _output.WriteLine($"CALL planning: requested depth={depth}; entered bodies={checks}; stack=512 KiB");
        await ReuseAsync(session);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - nested CALL execution exhaustion reports CG05008 and retains the session (#1807)")]
    public async Task Execute_CallEvaluationPastStack_ShouldRetainSession()
    {
        // Arrange: sibling iterators amplify execution stack use without deepening planner recursion.
        const int depth = 16;
        string source = "RETURN 1 AS y";
        string siblings = string.Concat(Enumerable.Range(0, 64).Select(i => " CALL () { RETURN 0 AS z" + i + " }"));
        for (int i = 0; i < depth; i++)
        { source = "LET marker=observe(1) CALL () { " + source + " }" + siblings + " RETURN y"; }
        var request = GraphQueryRequest.FromGql(source);
        int entered = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("call-execute-stack");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("observe", value => { entered++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = OnThread(() => session.ExecuteAsync(request, CancellationToken.None).AsTask(), 512 * 1024);
        // Assert: observing a body proves admission/planning finished before the stack refusal.
        var failure = error.ShouldBeOfType<DatabaseException>();
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05008");
        failure.InnerException.ShouldBeOfType<InsufficientExecutionStackException>();
        entered.ShouldBeInRange(1, depth - 1);
        _output.WriteLine($"CALL execution: requested depth={depth}; entered bodies={entered}; 64 siblings/body; stack=512 KiB");
        session.LastStatementMetrics!.Memory.ActiveLeases.ShouldBe(0);
        await ReuseAsync(session);
    }

    private static Exception? OnThread(Func<Task> action, int stackBytes)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action().GetAwaiter().GetResult(); }
            catch (Exception exception) { error = exception; }
        }, stackBytes);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        return error;
    }

    private static async Task ReuseAsync(GraphDatabaseSession session)
    {
        await using var result = (Execution.DatabaseResultSet)await session.ExecuteAsync("RETURN 7", cancellationToken: CancellationToken.None);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(7L); count++; }
        count.ShouldBe(1);
    }
}
