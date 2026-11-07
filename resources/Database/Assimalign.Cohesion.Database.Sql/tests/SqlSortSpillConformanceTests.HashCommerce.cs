using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Commerce hash shapes: default-grant annual aggregation completes; CTE/window and NOT IN owners retain their boundaries (#1789)")]
    public async Task ExecuteAsync_CommerceHashShapes_ShouldCompleteRootGroupAndReportRemainingOwners()
    {
        // Arrange: exact SQL shapes from cohesion-benchmarks SQL.md:775,835,915.
        // SF10 has 10m orders over the calendar and 500k products. Use a 1.5m-row
        // annual slice with 250k customers, six orders each, plus all 500k product keys.
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var builder = SqlDatabaseEngine.CreateBuilder("hash-commerce");
        builder.Options.TempDirectory = Path.Combine(_root, ".commerce");
        await using var engine = await builder.BuildAsync(guard.Token);
        await using var session = await (await engine.CreateDatabaseAsync("db", guard.Token)).CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE TABLE orders(order_id BIGINT,customer_id BIGINT,order_date TIMESTAMP,status VARCHAR(20),total NUMERIC(12,2))", cancellationToken: guard.Token);
        for (int first = 0; first < 1500000; first += 2000)
        {
            await session.ExecuteAsync("INSERT INTO orders VALUES" + string.Join(',', Enumerable.Range(first, 2000)
                .Select(i => $"({i},{i % 250000},TIMESTAMP '2025-06-01','paid',10.00)")), cancellationToken: guard.Token);
        }
        var parameters = new Dictionary<string, object?> { ["start"] = new DateTime(2025, 1, 1), ["end"] = new DateTime(2026, 1, 1) };
        const string repeat = "SELECT customer_id,COUNT(*) AS order_count,SUM(total) AS revenue FROM orders WHERE order_date>=@start AND order_date<@end GROUP BY customer_id HAVING COUNT(*)>=5 ORDER BY order_count DESC,customer_id LIMIT 100";
        var timer = Stopwatch.StartNew(); long partitioned = 0, ordered = 0;
        SqlPlanExecutor.ObserveHashSpill = (phase, _) =>
        { if (phase == "partitioned") { partitioned = timer.ElapsedTicks; } if (phase == "ordered") { ordered = timer.ElapsedTicks; } };
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(repeat, parameters, guard.Token);

            // Assert
            var rows = await Rows(result, guard.Token); rows.Length.ShouldBe(100);
            rows.Select(r => r.Cells[0]).ShouldBe(Enumerable.Range(0, 100).Select(i => (object)(long)i));
            rows.ShouldAllBe(r => Equals(r.Cells[1], 6L) && Equals(r.Cells[2], 60m));
            var metrics = session.LastStatementMetrics!;
            metrics.RecordsExamined.ShouldBe(1500000); metrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(67108864);
            var spill = SqlHashSpillMetrics.For(metrics).ShouldNotBeNull();
            _output.WriteLine($"COMMERCE_REPEAT|input=1500000|groups=250000|grant=67108864|peak={spill.PeakBytes}|batches={spill.Batches}|passes={spill.Passes}|runs={spill.Runs}|partition_ms={partitioned * 1000.0 / Stopwatch.Frequency:F3}|ordinal_ms={(ordered - partitioned) * 1000.0 / Stopwatch.Frequency:F3}|total_ms={timer.Elapsed.TotalMilliseconds:F3}");
        }
        finally { SqlPlanExecutor.ObserveHashSpill = null; }

        await session.ExecuteAsync("CREATE TABLE products(product_id BIGINT,category_id BIGINT)", cancellationToken: guard.Token);
        await session.ExecuteAsync("CREATE TABLE order_items(product_id BIGINT,line_total NUMERIC(12,2))", cancellationToken: guard.Token);
        await session.ExecuteAsync("CREATE TABLE shipments(order_id BIGINT)", cancellationToken: guard.Token);
        for (int first = 0; first < 800000; first += 2000)
        {
            if (first < 500000)
            {
                await session.ExecuteAsync("INSERT INTO products VALUES" + string.Join(',', Enumerable.Range(first, 2000).Select(i => $"({i},{i % 400})")), cancellationToken: guard.Token);
                await session.ExecuteAsync("INSERT INTO order_items VALUES" + string.Join(',', Enumerable.Range(first, 2000).Select(i => $"({i},1.00)")), cancellationToken: guard.Token);
            }
            await session.ExecuteAsync("INSERT INTO shipments VALUES" + string.Join(',', Enumerable.Range(first, 2000).Select(i => $"({i})")), cancellationToken: guard.Token);
        }
        string[] remaining =
        [
            "WITH revenues AS (SELECT p.category_id,p.product_id,SUM(i.line_total) AS revenue FROM products p INNER JOIN order_items i ON p.product_id=i.product_id GROUP BY p.category_id,p.product_id), ranked AS (SELECT category_id,product_id,revenue,RANK() OVER(PARTITION BY category_id ORDER BY revenue DESC) AS revenue_rank FROM revenues) SELECT category_id,product_id,revenue,revenue_rank FROM ranked WHERE revenue_rank<=3 ORDER BY category_id,revenue_rank,product_id",
            "SELECT order_id,customer_id,order_date,total FROM orders WHERE status='paid' AND order_date<@end AND order_id NOT IN(SELECT order_id FROM shipments) ORDER BY order_date,order_id",
        ];
        // These are explicit scope boundaries in SQ-L3a, rather than silently granting
        // nested bodies, window buffers or eager subquery vectors permission to spill.
        foreach (string sql in remaining)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, parameters, guard.Token));
            error.Diagnostics.ShouldContain(d => d.Code == "CS05067");
            var metrics = session.LastStatementMetrics!;
            _output.WriteLine($"COMMERCE_REMAINING|shape={(sql.StartsWith("WITH", StringComparison.Ordinal) ? "category-rank-window" : "unshipped-orders")}|code={error.Diagnostics[0].Code}|peak_memory={metrics.PeakHashMemoryBytes}|examined={metrics.RecordsExamined}|message={error.Message}");
            metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
            TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
    }
    [Fact(DisplayName = "Cohesion Test [Sql] - Unordered repeat customers proxy uses one ordinal sort and an output spool (#1789)")]
    public async Task ExecuteAsync_UnorderedRepeatCustomers_ShouldPreserveOrdinalOutput()
    {
        // Arrange
        using var guard=new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var builder=SqlDatabaseEngine.CreateBuilder("unordered-commerce");
        builder.Options.TempDirectory=System.IO.Path.Combine(_root,".repeat");
        builder.Options.StatementMemoryBudget=8388608; builder.Options.StatementTempSpaceQuota=268435456;
        await using var engine=await builder.BuildAsync(guard.Token);
        await using var session=await (await engine.CreateDatabaseAsync("db",guard.Token)).CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE TABLE orders(order_id BIGINT,customer_id BIGINT,order_date TIMESTAMP,total NUMERIC(12,2))",cancellationToken:guard.Token);
        for(int first=0;first<1500000;first+=2000)
        {
            await session.ExecuteAsync("INSERT INTO orders VALUES"+string.Join(',',Enumerable.Range(first,2000).Select(i=>$"({i},{i%250000},TIMESTAMP '2025-06-01',10.00)")),cancellationToken:guard.Token);
        }
        // Act
        var watch=Stopwatch.StartNew();
        await using var result=(DatabaseResultSet)await session.ExecuteAsync("SELECT customer_id,COUNT(*) AS order_count,SUM(total) AS revenue FROM orders WHERE order_date>=TIMESTAMP '2025-01-01' AND order_date<TIMESTAMP '2026-01-01' GROUP BY customer_id HAVING COUNT(*)>=5",cancellationToken:guard.Token);
        long count=0;
        // Assert
        await foreach(var row in result.GetRowsAsync(guard.Token))
        { row.GetInt64(0).ShouldBe(count++); row.GetInt64(1).ShouldBe(6); row.GetValue(2).ShouldBe(60m); }
        count.ShouldBe(250000);
        var h=SqlHashSpillMetrics.For(session.LastStatementMetrics!)!;var sort=SqlSortSpillMetrics.For(session.LastStatementMetrics!);
        _output.WriteLine($"HASH_REPEAT_UNORDERED|rows={count}|hash_bytes={h.PhysicalBytes}|hash_peak={h.PeakBytes}|sort_bytes={sort?.PhysicalBytes}|sort_peak={sort?.PeakBytes}|ms={watch.Elapsed.TotalMilliseconds:F3}");
    }
}
