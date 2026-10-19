using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSubqueryExecutionTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: twenty nested aggregates summarize each dependency once")]
    public async Task Plan_TwentyAggregateLevels_ShouldAvoidRepeatedDependencyWalks()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("aggregate-planning-cost");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
        string sql = "SELECT SUM(id) FROM t";
        for (int depth = 0; depth < 20; depth++) { sql = $"SELECT SUM(({sql})) FROM t"; }
        var expression = SqlQueryRequest.FromSql(sql).Statement.SqlExpression;

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var plan = new SqlPlanner(database.Catalog, null).Plan(expression);
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        plan.ShouldNotBeNull();
        allocated.ShouldBeLessThan(4_000_000);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        (await RowsAsync(session, sql)).ShouldHaveSingleItem()[0].ShouldBe(1m);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: query planning observes the statement cancellation token")]
    public async Task Plan_CanceledNestedQuery_ShouldThrowCancellation()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("planning-cancel");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        var expression = SqlQueryRequest.FromSql("SELECT d.id, (SELECT id FROM t) FROM (SELECT id FROM t) d").Statement.SqlExpression;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act / Assert
        Should.Throw<OperationCanceledException>(() => new SqlPlanner(database.Catalog, null,
            cancellationToken: cancellation.Token).Plan(expression)).CancellationToken.ShouldBe(cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: twenty derived levels plan within bounded time and allocation")]
    public async Task Plan_TwentyDerivedLevels_ShouldBindEachBodyOnce()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("planning-cost");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1);");
        string sql = "SELECT id FROM t";
        for (int depth = 0; depth < 20; depth++) { sql = $"SELECT d.id, (SELECT id FROM t) AS s FROM ({sql}) d"; }
        var expression = SqlQueryRequest.FromSql(sql).Statement.SqlExpression;
        new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql("SELECT id FROM t").Statement.SqlExpression);

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var plan = new SqlPlanner(database.Catalog, null).Plan(expression);
        stopwatch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        plan.ShouldNotBeNull();
        Slots(plan).Order().ShouldBe(Enumerable.Range(0, 20));
        _correlationOutput.WriteLine($"20-level planning: {stopwatch.Elapsed.TotalMilliseconds:F3} ms; {allocated:N0} allocated bytes.");
        allocated.ShouldBeLessThan(4_000_000);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        (await RowsAsync(session, sql)).ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 1 });

        static IEnumerable<int> Slots(SqlPlan plan)
        {
            if (plan is SqlSubqueryPlan subqueries)
            {
                foreach (var binding in subqueries.Queries)
                {
                    yield return binding.Slot;
                    foreach (int slot in Slots(binding.Query)) { yield return slot; }
                }
                foreach (int slot in Slots(subqueries.Input)) { yield return slot; }
            }
            if (plan is SqlJoinPlan join)
            {
                foreach (var binding in join.Bindings)
                {
                    if (binding.Source.Derived is not { } derived) { continue; }
                    foreach (int slot in Slots(derived.Query)) { yield return slot; }
                }
            }
        }
    }
}
