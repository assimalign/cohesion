using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Base staged sort keeps its full grant before join spill (#1789)")]
    public async Task ExecuteAsync_JoinFix3StagedSort_ShouldKeepBaseBoundary()
    {
        // Arrange: solve the exact framed-record boundary, forty bytes below base's bound.
        const long budget = 262144;
        int width = 0, bound = 0, framed = 0;
        for (int n = 5100; n < 5400; n++)
        {
            string text = new('x', n);
            SqlTempRowCodec.TryGetLength([0, 0, text], out int source);
            SqlTempRowCodec.TryGetLength([0L, text], out int output);
            SqlTempSortKeyCodec.TryEncode([0], [new(false, true, Collation.Binary)], new byte[1], out var key);
            int candidate = (int)((budget - 8192 - source - 32768) / 16 - 8192);
            if (candidate - (16 + key.Length + Math.Max(source, output)) == 40)
            { width = n; bound = candidate; framed = 16 + key.Length + Math.Max(source, output); break; }
        }
        width.ShouldBeGreaterThan(0);
        var calls = new List<long>();
        await using var engine = await CreateFix2("fix3-staged", budget, calls);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT,k INT,note TEXT)");
        await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 201).Select(i => $"({i},{(i == 200 ? 0 : i + 1)},'{new string('x', i == 200 ? width : 1000)}')")));
        _output.WriteLine($"FIX3_STAGED_BOUND|width={width}|bound={bound}|framed={framed}|margin={bound - framed}");
        // Act
        var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT stamp(id),note FROM t ORDER BY k");
        try
        {
            var rows = await Rows(result);
            rows.Length.ShouldBe(201);
            var order = new long[] { 200 }.Concat(Enumerable.Range(0, 200).Select(i => (long)i)).ToArray();
            rows.Select(row => row.Cells[0]).ShouldBe(order.Cast<object?>());
            rows[0].Cells[1].ShouldBe(new string('x', width));
            calls.ShouldBe(order);
            _output.WriteLine($"FIX3_STAGED|width={width}|bound={bound}|framed={framed}|digest={Fix2Fingerprint(rows)}|calls={calls.Count}|examined={session.LastStatementMetrics!.RecordsExamined}|pairs={session.LastStatementMetrics.JoinCandidatePairs}");
        }
        finally { await result.DisposeAsync(); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Staged grouping preserves base group admission (#1789)")]
    [InlineData(300)]
    public async Task ExecuteAsync_JoinFix3Group_ShouldKeepBaseAdmission(int groups)
    {
        // Arrange
        var calls = new List<long>();
        await using var engine = await CreateFix2("fix3-group", 262144, calls, quota: 67108864);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT,note TEXT)");
        for (int start = 0; start < groups * 2; start += 100)
        { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(start, Math.Min(100, groups * 2 - start)).Select(i => $"({i},'{new string('x', 512)}')"))); }
        bool staged = false;
        long admission = -1;
        SqlRowCollector.ObserveSpill = phase => staged |= phase == "staged-drained";
        SqlPlanExecutor.ObserveHashSpill = (phase, value) => { if (phase == "group-admission" && admission < 0) { admission = value; } };
        // Act
        try
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT stamp(id/2),BOOL_AND(note IS NULL) FROM t GROUP BY stamp(id/2) HAVING COUNT(*)<0");
            (await Rows(result)).ShouldBeEmpty();
        }
        finally { SqlRowCollector.ObserveSpill = null; SqlPlanExecutor.ObserveHashSpill = null; }
        // Assert
        var metrics = session.LastStatementMetrics!;
        _output.WriteLine($"FIX3_GROUP|groups={groups}|peak={metrics.PeakHashMemoryBytes}|batches={SqlHashSpillMetrics.For(metrics)?.Batches ?? 0}|examined={metrics.RecordsExamined}");
        (SqlHashSpillMetrics.For(metrics)?.Batches ?? 0).ShouldBe(0);
        staged.ShouldBeTrue();
        SqlTempRowCodec.TryGetLength([0, new string('x', 512)], out int recordBytes).ShouldBeTrue();
        admission.ShouldBe(262144 - 8192 - recordBytes);
        calls.ShouldBe(Enumerable.Range(0, 2 * groups).Select(i => (long)(i / 2)));
        metrics.RecordsExamined.ShouldBe(2 * groups); metrics.JoinCandidatePairs.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - A non-join collector retains base's delivered reader grant (#1789)")]
    [InlineData(128)]
    public async Task ExecuteAsync_JoinFix3Collector_ShouldKeepBaseReaderAdmission(int cells)
    {
        // Arrange: distinct boxed booleans on decode make the delivered row wider than its frame.
        await using var engine = await CreateFix2("fix3-collector", 262144, quota: 67108864);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT)");
        await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 600).Select(i => $"({i})")));
        string sql = "SELECT id," + string.Join(',', Enumerable.Range(0, cells).Select(i => $"TRUE AS b{i}")) + " FROM t ORDER BY id";
        // Act
        var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
        try
        {
            _output.WriteLine("FIX3_READER|" + string.Join(",", typeof(SqlSpilledResultSet).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Where(f => f.FieldType == typeof(long)).Select(f => f.Name + "=" + f.GetValue(result))));
            typeof(SqlSpilledResultSet).GetField("_readerGrant", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(result).ShouldBe(262144L);
            await using var first = result.GetRowsAsync().GetAsyncEnumerator();
            await using var second = result.GetRowsAsync().GetAsyncEnumerator();
            (await first.MoveNextAsync()).ShouldBeTrue(); (await second.MoveNextAsync()).ShouldBeTrue();
            await first.DisposeAsync(); await second.DisposeAsync();
            var rows = await Rows(result); rows.Length.ShouldBe(600);
            rows.Select(row => row.Cells[0]).ShouldBe(Enumerable.Range(0, 600).Cast<object?>());
            rows.SelectMany(row => row.Cells.Skip(1)).All(value => value is true).ShouldBeTrue();
            session.LastStatementMetrics!.RecordsExamined.ShouldBe(600); session.LastStatementMetrics.JoinCandidatePairs.ShouldBe(0);
            _output.WriteLine($"FIX3_COLLECTOR|cells={cells}|rows={rows.Length}|digest={Fix2Fingerprint(rows)}|examined={session.LastStatementMetrics!.RecordsExamined}|pairs={session.LastStatementMetrics.JoinCandidatePairs}");
        }
        finally { await result.DisposeAsync(); }
    }
}
