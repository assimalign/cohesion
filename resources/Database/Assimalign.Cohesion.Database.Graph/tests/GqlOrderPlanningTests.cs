using System;
using System.Diagnostics;
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

public sealed class GqlOrderPlanningTests
{
    private readonly ITestOutputHelper _output;
    public GqlOrderPlanningTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering: fifty thousand projections and keys plan with a linear structural index")]
    public async Task Plan_LargeProjectionAndKeyLists_ShouldFinishWithinBoundAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("large-order-plan", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var projections = Enumerable.Range(0, 50_000).Select(i => new GqlProjection(new GqlLiteralExpression((long)i), "c" + i)).ToArray();
        var keys = Enumerable.Range(0, 50_000).Select(_ => new GqlSortItem(new GqlLiteralExpression(-1L))).ToArray();
        var query = new GqlQueryExpression([], null, [], [], false, projections) { OrderBy = keys };
        var nested = new GqlQueryExpression([], null, [], [], false, projections)
        { OrderBy = [new(new GqlListExpression(keys.Select(key => key.Expression).ToArray()))] };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var watch = Stopwatch.StartNew();
        // Act
        var plan = await database.RunAsync(session, operation => new ValueTask<GraphPlan>(
            new GraphPlanner(database, operation.Context.Snapshot).Plan(query, token: timeout.Token)), timeout.Token);
        var nestedPlan = await database.RunAsync(session, operation => new ValueTask<GraphPlan>(
            new GraphPlanner(database, operation.Context.Snapshot).Plan(nested, token: timeout.Token)), timeout.Token);
        // Assert
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        plan.SortPage!.Keys.Count.ShouldBe(50_000);
        plan.SortPage.Keys.ShouldAllBe(key => key.ProjectionIndex == -1);
        nestedPlan.SortPage!.Keys.ShouldHaveSingleItem().ProjectionIndex.ShouldBe(-1);
        _output.WriteLine($"50,000 projections with 50,000 keys, then a 50,000-element key: {watch.Elapsed.TotalMilliseconds:F1} ms");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Ordering: cancellation interrupts planning through ExecuteAsync")]
    public async Task Execute_CancelDuringPlanning_ShouldReleaseSessionAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("cancel-order-plan", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var projections = new CancelingReadOnlyList<GqlProjection>(Enumerable.Range(0, 50_000)
            .Select(i => new GqlProjection(new GqlLiteralExpression((long)i), "c" + i)).ToArray(), cancellation, 128);
        var query = new GqlQueryExpression([], null, [], [], false, projections)
        { OrderBy = [new(new GqlLiteralExpression(-1L))] };
        // Act
        var error = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
            new GraphQueryRequest(new GqlQueryStatement(query)), cancellation.Token));
        // Assert
        projections.Reads.ShouldBe(128);
        error.CancellationToken.ShouldBe(cancellation.Token);
        await using var reused = (await session.ExecuteAsync("RETURN 1", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        reused.Status.ShouldBe(DatabaseResultStatus.Success);
    }
}
