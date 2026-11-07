using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlNaturalJoinCancellationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - NATURAL binding: cancellation after the first input stops a long chain before allocating its scopes (#1792)")]
    public async Task Bind_LongNaturalChain_ShouldObserveCancellationAtJoin()
    {
        // Arrange: the parameter read records that planning passed its entry check and bound its first input.
        await using var engine = SqlDatabaseEngine.Create("natural-cancel", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        for (int i = 0; i < 1000; i++)
        {
            await session.ExecuteAsync("CREATE TABLE t" + i + "(" + string.Join(",", Enumerable.Range(0, 8).Select(c => "c" + i + "_" + c + " INT")) + ")", cancellationToken: CancellationToken.None);
        }
        var expression = SqlQueryRequest.FromSql("SELECT 1 FROM (SELECT @gate AS marker) seed NATURAL JOIN " +
            string.Join(" NATURAL JOIN ", Enumerable.Range(0, 1000).Select(i => "t" + i))).Statement.SqlExpression;
        using var cancellation = new CancellationTokenSource();
        var parameters = new CancelingBindParameters(cancellation);
        var planner = new SqlPlanner(database.Catalog, parameters, cancellationToken: cancellation.Token);
        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        var failure = Should.Throw<OperationCanceledException>(() => planner.Plan(expression));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Assert: a deterministic progress witness and allocation ceiling, with no clock bound or race.
        parameters.Reads.ShouldBeGreaterThan(0);
        failure.CancellationToken.ShouldBe(cancellation.Token);
        allocated.ShouldBeLessThan(1024 * 1024);
    }
}
