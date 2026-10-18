using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentPlannerScaleTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Planning: 200,000 distinct targets and projection names complete within twenty seconds")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_ManyDistinctNames_ShouldCompleteWithinTimeBoundAsync(bool update)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("planning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        const int count = 200_000;
        string names = string.Join(",", Enumerable.Range(0, count).Select(i => update ? $"root.a{i} = 1" : $"1 AS a{i}"));
        string statement = update ? "UPDATE items SET " + names : "SELECT " + names + " FROM items";
        var bound = TimeSpan.FromSeconds(20);
        // Act
        var watch = Stopwatch.StartNew();
        var plan = await DocumentPlanTestHelper.PlanAsync(database, statement, cancellationToken: CancellationToken.None);
        watch.Stop();
        // Assert
        watch.Elapsed.ShouldBeLessThan(bound);
        if (update) { plan.ShouldBeOfType<DocumentUpdatePlan>().Targets.Count.ShouldBe(count); }
        else { plan.ShouldBeOfType<DocumentPlan>().Logical.Projections.Count.ShouldBe(count); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Planning: UPDATE cancellation is observed while adding targets")]
    public async Task Plan_CancelledDuringTargetValidation_ShouldStopAtNextTargetAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("planning", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        var parsed = DocumentQueryRequest.FromOql("UPDATE items SET a = 1").Statement.OqlExpression.ShouldBeOfType<OqlUpdateExpression>();
        using var cancellation = new CancellationTokenSource();
        var assignments = new CancellingReadOnlyList<OqlUpdateAssignment>(parsed.Assignments, cancellation);
        var statement = new OqlUpdateExpression("items", null, assignments, [], null);
        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await database.RunAsync(session, operation =>
            ValueTask.FromResult(new DocumentPlanner(database.Catalog, operation.Context.Snapshot, null, cancellation.Token).Plan(statement)), CancellationToken.None));
        // Assert
        cancellation.IsCancellationRequested.ShouldBeTrue();
        assignments.ReadCount.ShouldBe(1);
    }
}
