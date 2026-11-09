#if !DEBUG
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
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

    [Theory(DisplayName = "Cohesion Test [Sql] - CASE/COALESCE: Decimal identity alternatives keep base row allocations (#1788)")]
    [InlineData("CASE WHEN flag THEN amount END")]
    [InlineData("CASE WHEN flag THEN amount ELSE 0.0 END")]
    public async Task Execute_DecimalCaseInput_ShouldKeepBaseRowAllocations(string expression)
    {
        // Arrange: the two-column table isolates the Decimal CASE input cost.
        // Base 17e22bb08, 5000 -> 10000 rows, measured on ARM64 and --arch x64:
        // no ELSE: 1,890,736 -> 3,772,040 bytes; 376.2608 B/row on each architecture.
        // ELSE 0.0: 1,891,104 -> 3,772,408 bytes; 376.2608 B/row on each architecture.
        // Both ceilings are the measured architecture's base slope plus 4 B/row.
        await using var engine = SqlDatabaseEngine.Create("common-allocation", new());
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(flag BOOLEAN,amount NUMERIC)");
        string insert = "INSERT INTO t VALUES " + string.Join(',', Enumerable.Repeat("(TRUE,1.25)", 100));
        for (int index = 0; index < 50; index++) { await session.ExecuteAsync(insert); }
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = SqlQueryRequest.FromSql($"SELECT SUM({expression}) FROM t");

        // Act
        long small = Minimum();
        for (int index = 0; index < 50; index++) { await session.ExecuteAsync(insert); }
        long large = Minimum();
        double slope = (large - small) / 5000d;

        // Assert
        _output.WriteLine($"{RuntimeInformation.ProcessArchitecture} {expression}: 5000={small}; 10000={large}; slope={slope:F4} B/row");
        slope.ShouldBeLessThanOrEqualTo(376.2608 + 4);

        long Minimum()
        {
            for (int warmup = 0; warmup < 4; warmup++) { Measure(); }
            long minimum = long.MaxValue;
            for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure()); }
            return minimum;
        }

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
