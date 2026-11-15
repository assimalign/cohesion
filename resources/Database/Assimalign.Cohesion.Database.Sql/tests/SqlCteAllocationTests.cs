#if !DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlCteAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SqlCteAllocationTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - WITH: non-recursive execution retains base allocations")]
    public async Task Execute_NonRecursiveCommonTable_ShouldRetainBaseAllocations()
    {
        // Arrange: parsing and setup are outside the measured synchronous execution path.
        await using var engine = SqlDatabaseEngine.Create("cte-allocation", new() { TempDirectory = SqlAllocationTemp.Directory });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (7)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = SqlQueryRequest.FromSql("WITH a AS (SELECT id FROM t), b AS (SELECT id FROM a) SELECT id FROM b");
        Measure(1_000);

        // Act
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure(5_000)); }

        // Assert
        _output.WriteLine($"CTE bytes/query: {minimum / 5000d:F4}");
        // Base 3a4b483d8 engine: 21,672 bytes/query, minimum of three 5,000-query rounds.
        minimum.ShouldBeLessThanOrEqualTo(108_360_000);

        long Measure(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < count; index++)
            {
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                pending.IsCompletedSuccessfully.ShouldBeTrue();
                var rows = pending.Result.ShouldBeAssignableTo<DatabaseResultSet>();
                var disposal = rows.DisposeAsync();
                disposal.IsCompletedSuccessfully.ShouldBeTrue();
                disposal.GetAwaiter().GetResult();
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
    [Theory(DisplayName = "Cohesion Test [Sql] - Nested grouping and derived joins with temp space retain batch-base allocations (#1789)")]
    [InlineData("WITH g AS (SELECT a,COUNT(*) FROM t GROUP BY a) SELECT * FROM g", 98120000L)]
    [InlineData("SELECT d.a FROM (SELECT a FROM t) d JOIN t ON d.a=t.a", 100000000L)]
    public async Task Execute_NestedSpillPermission_ShouldRetainBaseAllocations(string sql, long pin)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("nested-allocation", new() { TempDirectory = SqlAllocationTemp.Directory });
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(a INT)"); await session.ExecuteAsync("INSERT INTO t VALUES(7)");
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var request = SqlQueryRequest.FromSql(sql);
        Measure(1000);
        // Act
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure(5000)); }
        // Assert: pins measured against c9ac0df64 on ARM64 and x64.
        _output.WriteLine($"NESTED_ALLOC|sql={sql}|bytes={minimum}|perQuery={minimum / 5000d}");
        minimum.ShouldBe(pin);
        long Measure(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                pending.IsCompletedSuccessfully.ShouldBeTrue();
                var result = pending.Result.ShouldBeAssignableTo<DatabaseResultSet>();
                var disposal = result.DisposeAsync(); disposal.IsCompletedSuccessfully.ShouldBeTrue(); disposal.GetAwaiter().GetResult();
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
#endif
