#if !DEBUG
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlCommonTypeAllocationTests
{
    private readonly ITestOutputHelper _output;

    public SqlCommonTypeAllocationTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: Decimal identity alternatives keep base row allocations (#1788)")]
    public async Task Execute_DecimalCaseInput_ShouldKeepBaseRowAllocations()
    {
        // Arrange: the two-column table isolates the Decimal CASE input cost.
        // The cap leaves 12 bytes/row for runtime/architecture variation while still
        // detecting 58b85171a's extra 32-byte Decimal box on every qualifying row.
        await using var engine = SqlDatabaseEngine.Create("common-allocation", new());
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(flag BOOLEAN,amount NUMERIC)");
        string insert = "INSERT INTO t VALUES " + string.Join(',', Enumerable.Repeat("(TRUE,1.25)", 100));
        for (int index = 0; index < 100; index++) { await session.ExecuteAsync(insert); }
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = SqlQueryRequest.FromSql("SELECT SUM(CASE WHEN flag THEN amount END) FROM t");
        for (int warmup = 0; warmup < 4; warmup++) { Measure(); }

        // Act
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure()); }

        // Assert
        _output.WriteLine($"Decimal CASE: {minimum} bytes / 10000 rows = {minimum / 10000d:F4} bytes/row");
        minimum.ShouldBeLessThanOrEqualTo(3_900_000);

        long Measure()
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            var pending = session.ExecuteAsync(request, CancellationToken.None);
            pending.IsCompletedSuccessfully.ShouldBeTrue();
            var rows = (DatabaseResultSet)pending.Result;
            var disposal = rows.DisposeAsync();
            disposal.IsCompletedSuccessfully.ShouldBeTrue();
            disposal.GetAwaiter().GetResult();
            return GC.GetAllocatedBytesForCurrentThread() - start;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: grouped nesting retains quadratic growth (#1788)")]
    public async Task Plan_GroupedCoalesceNest_ShouldKeepBaseGrowth()
    {
        // Arrange: the same HAVING shape as SqlExpressionDepthExecutionTests.
        await using var engine = SqlDatabaseEngine.Create("common-depth", new()
        {
            ExpressionNestingLimit = 4096,
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(a INT,b INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES(1,1)");
        using var thread = new SqlDepthTestThread(32 * 1024);

        // Act: minima on one dedicated thread; only their ratio decides the result.
        double small = Minimum(256), large = Minimum(1024);

        // Assert: base's fourfold depth costs about sixteenfold work. A generous
        // factor of 32 allows noise while refusing head's measured factor of 64.7.
        _output.WriteLine($"Grouped nesting: depth256={small:F3} ms; depth1024={large:F3} ms");
        large.ShouldBeLessThanOrEqualTo(small * 32);

        double Minimum(int depth)
        {
            string sql = "SELECT b,COUNT(*) FROM t GROUP BY b HAVING " + string.Concat(Enumerable.Repeat("COALESCE(NULL,", depth))
                + "MAX(a)" + new string(')', depth) + ">0";
            var result = thread.Run(() =>
            {
                double minimum = double.MaxValue;
                for (int round = 0; round < 3; round++)
                {
                    var watch = Stopwatch.StartNew();
                    var rows = (DatabaseResultSet)session.ExecuteAsync(sql).GetAwaiter().GetResult();
                    rows.DisposeAsync().GetAwaiter().GetResult();
                    minimum = Math.Min(minimum, watch.Elapsed.TotalMilliseconds);
                }
                return minimum;
            });
            result.Failure.ShouldBeNull();
            return result.Result;
        }
    }
}
#endif
