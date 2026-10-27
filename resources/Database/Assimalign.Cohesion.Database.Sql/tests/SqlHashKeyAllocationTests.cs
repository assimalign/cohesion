#if !DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlHashKeyAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SqlHashKeyAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Ordinary column equijoin hash-key extraction retains its base allocation budget (#1525)")]
    public async Task HashKeys_ColumnEquijoin_ShouldRetainBaseAllocationBudget()
    {
        // Arrange: parse and bind once, outside the measured extraction calls.
        await using var engine = SqlDatabaseEngine.Create("hash-key-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE a(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE b(k INT)", cancellationToken: CancellationToken.None);
        var request = SqlQueryRequest.FromSql("SELECT * FROM a JOIN b ON a.k = b.k");
        var plan = new SqlPlanner(database.Catalog, null).Plan(request.Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
        var step = plan.Steps.ShouldHaveSingleItem();
        var keys = SqlPlanner.HashKeys(step.Condition, plan.Bindings, plan.Columns, step.Binding).ShouldNotBeNull();
        keys.OuterOrdinals.ShouldBe([0]);
        keys.InnerOrdinals.ShouldBe([0]);
        keys.OuterExpressions.ShouldBeNull();
        keys.InnerExpressions.ShouldBeNull();
        const int iterations = 1000;
        void Extract()
        {
            for (int i = 0; i < iterations; i++)
            {
                GC.KeepAlive(SqlPlanner.HashKeys(step.Condition, plan.Bindings, plan.Columns, step.Binding));
            }
        }
        Extract();

        // Act
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Extract();
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        _output.WriteLine($"Ordinary column-equijoin hash-key extraction: {minimum / (double)iterations} bytes/call");

        // Assert: f50f21b07 measured 1,040 bytes/call (minimum of three 1,000-call Release rounds).
        minimum.ShouldBeLessThanOrEqualTo(1040L * iterations);
    }
}
#endif
