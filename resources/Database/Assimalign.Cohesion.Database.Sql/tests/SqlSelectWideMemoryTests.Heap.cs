using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectWideMemoryTests
{
    public static IEnumerable<object[]> HeapBuffers()
    {
        long[][] charges =
        [
            [655448, 655448, 655448, 655448],
            [1245272, 1245272, 1245272, 1245272],
            [131432, 131432, 131432, 131432],
            [1974448, 1974448, 1974448, 1974448],
            [1179928, 3407960, 1179928, 3407960],
            [1442472, 3801560, 1442472, 3801560],
            [3211688, 5963752, 3211688, 5963752],
            [4137136, 4923568, 4137136, 4923568],
            [4888, 791320, 4888, 791320],
            [6885464, 7671896, 6885464, 7671896],
            [2761456, 3547888, 2761456, 3547888],
            [7225792, 8012224, 7225792, 8012224],
            [3743888, 4530320, 3743888, 4530320],
            [2830600, 2830600, 2830600, 2830600],
            [1712304, 1712304, 1712304, 1712304],
            [2228400, 3211528, 2228400, 3211528],
            [852144, 852144, 852144, 852144],
            [1114168, 1114168, 1114168, 1114168],
            [2425008, 2425008, 2425008, 2425008],
            [2556208, 2556208, 2556208, 2556208],
            [1457880, 1457880, 868056, 868056],
            [1376344, 1376344, 1376344, 1376344],
            [1146352, 1998256, 1146352, 1998256],
            [34603008, 34603008, 34603008, 34603008],
            [8781824, 8781824, 8781824, 8781824],
        ];
        foreach (var row in Buffers())
        {
            int shape = (int)row[0], path = (bool)row[2] ? 1 : 0;
            yield return [row[0], row[1], row[2], charges[shape][path], charges[shape][path + 2]];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - wide SELECT memory: scaled forced-GC measurements expose retained-heap ratios for every buffer (#1776)")]
    [MemberData(nameof(HeapBuffers))]
    public async Task ExecuteAsync_BufferHeap_ShouldReportMeasuredGrowth(int shape, string sql, bool forceBase, long expectedPeak, long expectedObserved)
    {
        // Arrange: warm the shape, then measure three fresh payloads. The largest growth
        // exposes undercharge; planning, fixed headers and CTAS storage are not SELECT rows.
        const int count = 8192;
        var builder = SqlDatabaseEngine.CreateBuilder("wide-buffer-heap");
        builder.Options.StatementMemoryBudget = 1L << 30;
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string command in new[]
        {
            "CREATE SEQUENCE s", "CREATE TABLE t(k INT)",
            "INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(1, count).Select(k => $"({k})")),
            "CREATE TABLE u(k INT)", "INSERT INTO u VALUES(1),(2)",
            "CREATE TABLE b(doc TEXT)", $"INSERT INTO b VALUES('{new string('x', 8000)}')",
            "CREATE TABLE equal_text(k INT,doc TEXT)",
        }) { await session.ExecuteAsync(command, cancellationToken: CancellationToken.None); }
        await session.ExecuteAsync("INSERT INTO equal_text SELECT k,@seed FROM t",
            new Dictionary<string, object?> { ["seed"] = new string('x', 2000) }, CancellationToken.None);
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        bool tableAs = sql.StartsWith("CREATE TABLE", StringComparison.Ordinal);
        bool hasParameter = sql.Contains("@p", StringComparison.Ordinal);
        bool comparesWide = sql.Contains("DISTINCT", StringComparison.Ordinal) || sql.Contains("GROUP BY", StringComparison.Ordinal)
            || sql.Contains("MAX(", StringComparison.Ordinal) || sql.Contains("UNION", StringComparison.Ordinal) || sql.Contains("INTERSECT", StringComparison.Ordinal);
        int length = tableAs ? 8000 : comparesWide ? 2048 : 65536;
        (await Execute(hasParameter ? new Dictionary<string, object?> { ["p"] = new string('x', length) } : null)).ShouldBe(expectedPeak);
        if (tableAs) { await session.ExecuteAsync("DROP TABLE dst", cancellationToken: CancellationToken.None); }
        long largestGrowth = long.MinValue, observedCharge = 0;
        Dictionary<string, object?>? parameters = null;

        // Act
        for (int round = 0; round < 3; round++)
        {
            parameters = null;
            await session.ExecuteAsync("SELECT 0", cancellationToken: CancellationToken.None);
            long before = GC.GetTotalMemory(true);
            if (hasParameter) { parameters = new Dictionary<string, object?> { ["p"] = new string('x', length) }; }
            SqlStatementMetrics? active = null;
            bool captured = false;
            try
            {
                SqlStatementMetrics.AfterMemoryReservation = (metrics, _) =>
                {
                    active = metrics;
                    if (!tableAs && !captured && metrics.HashMemoryBytes == expectedPeak)
                    { Capture(metrics.HashMemoryBytes); }
                };
                // Observe CTAS admission before writes add persistent storage to GC growth.
                if (tableAs) { SqlPlanExecutor.TableAsSourceAdmitted = _ => Capture(active!.HashMemoryBytes); }
                (await Execute(parameters)).ShouldBe(expectedPeak);
            }
            finally { SqlStatementMetrics.AfterMemoryReservation = null; SqlPlanExecutor.TableAsSourceAdmitted = null; }
            captured.ShouldBeTrue();
            if (tableAs) { await session.ExecuteAsync("DROP TABLE dst", cancellationToken: CancellationToken.None); }

            void Capture(long charge)
            {
                captured = true;
                largestGrowth = Math.Max(largestGrowth, GC.GetTotalMemory(true) - before);
                observedCharge = charge;
            }
        }

        // Assert: raw GC growth deliberately remains visible, including uncharged fixed state.
        largestGrowth.ShouldBeGreaterThan(0);
        observedCharge.ShouldBe(expectedObserved);
        _output.WriteLine($"BUFFER_HEAP|{shape}|base={forceBase}|peak={expectedPeak}|observedCharge={observedCharge}|heap={largestGrowth}|ratio={observedCharge / (double)largestGrowth:F6}");
        GC.KeepAlive(parameters);

        async Task<long> Execute(Dictionary<string, object?>? arguments)
        {
            var result = await session.ExecuteAsync(sql, arguments, CancellationToken.None);
            if (result is SqlMaterializedResultSet rows)
            {
                await rows.DisposeAsync();
                rows.ClearRows();
            }
            var metrics = session.LastStatementMetrics.ShouldNotBeNull();
            metrics.HashMemoryBytes.ShouldBe(0);
            metrics.PlainSelectMemoryBytes.ShouldBe(0);
            return metrics.PeakHashMemoryBytes;
        }
    }
}
