using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Spill memory: forced GC proves the handoff releases drained identities and delivery stays bounded (#1789)")]
    public async Task ExecuteAsync_LiveObjects_ShouldFitGrantAcrossHandoffAndDelivery()
    {
        // Arrange: only the first 200 objects have weak observers; the test keeps no output list.
        const long grant = 1048576, constant = 524288;
        var weak = new List<WeakReference>();
        var samples = new Dictionary<string, long>();
        long baseline = 0; bool observe = false, afterHandoff = false;
        long Sample(string phase)
        {
            long live = Math.Max(0, GC.GetTotalMemory(true) - baseline);
            samples[phase] = Math.Max(samples.GetValueOrDefault(phase), live);
            return live;
        }
        var builder = SqlDatabaseEngine.CreateBuilder("live-spill");
        builder.Options.RootPath = System.IO.Path.Combine(_root, "live");
        builder.Options.StatementMemoryBudget = grant;
        builder.Options.StatementTempSpaceQuota = 64 * 1024 * 1024;
        builder.Functions.Add(SqlScalarFunction.Create("fresh", (long value) =>
        {
            string result = new string((char)('a' + value % 23), 512);
            if (observe && value < 200) { weak.Add(new WeakReference(result)); }
            if (observe && afterHandoff && value % 701 == 0) { Sample("input"); }
            return result;
        }));
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(session);
        const string sql = "SELECT fresh(id) AS value FROM t ORDER BY value,id";
        await using (var warm = (DatabaseResultSet)await session.ExecuteAsync(sql)) { }
        baseline = GC.GetTotalMemory(true); observe = true;
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase is "handoff" or "drained" or "arena") { Sample(phase); }
            if (phase == "drained") { weak.Count.ShouldBe(200); weak.Count(w => w.IsAlive).ShouldBe(0); afterHandoff = true; }
        };
        try
        {
            // Act
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql);
            result.ShouldBeOfType<SqlSpilledResultSet>();
            Sample("before-delivery");
            int count = 0;
            await foreach (var row in result.GetRowsAsync())
            { row.GetString(0)!.Length.ShouldBe(512); if (++count % 701 == 0) { Sample("delivery"); } }
            count.ShouldBe(4000);

            // Assert
            foreach (string phase in new[] { "handoff", "drained", "arena", "input", "before-delivery", "delivery" })
            {
                samples.ContainsKey(phase).ShouldBeTrue();
                _output.WriteLine($"LIVE|{phase}|bytes={samples[phase]}|grant={grant}|constant={constant}");
                samples[phase].ShouldBeLessThanOrEqualTo((long)(grant * 1.1) + constant);
            }
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        finally { SqlRowCollector.ObserveSpill = null; }

        // Watch the row vectors themselves as well as their payload identities. Clearing
        // cells in a still-rooted buffer is insufficient evidence that drained rows died.
        var vectors = new List<WeakReference>(); bool vectorsDrained = false;
        var vectorMetrics = new SqlStatementMetrics();
        using var collector = new SqlRowCollector([new(SqlBoundConstant.Null, false, true, new SqlBoundCollation(Collation.Binary))],
            null, null, null, CancellationToken.None, vectorMetrics, grant);
        SqlRowCollector.ObserveSpill = phase =>
        {
            if (phase != "drained") { return; }
            GC.GetTotalMemory(true); vectors.Count.ShouldBe(200);
            vectors.Count(reference => reference.IsAlive).ShouldBe(0); vectorsDrained = true;
        };
        try
        {
            for (int i = 0; i < 4000; i++)
            {
                object?[] row = [i, new string('v', 512)];
                if (i < 200) { vectors.Add(new WeakReference(row)); }
                collector.Add(row, [i], session);
            }
            await using var vectorResult = collector.TakeSpilled([], 0, -1);
            vectorsDrained.ShouldBeTrue(); vectorMetrics.HashMemoryBytes.ShouldBe(0);
            vectorMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            _output.WriteLine("WEAK_ROWS|vectors=200|alive=0");
        }
        finally { SqlRowCollector.ObserveSpill = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Spill capacity: default quota accepts ten ASCII grants and pins both sides for three-byte text (#1789)")]
    [InlineData(20, false, 10, true)]
    [InlineData(50, false, 10, true)]
    [InlineData(20, true, 6, true)]
    [InlineData(20, true, 7, false)]
    public async Task ExecuteAsync_TenGrantCapacity_ShouldHonorDefaultQuota(int characters, bool threeByte, int multiple, bool succeeds)
    {
        // Arrange
        const int grant = 262144;
        var builder = SqlDatabaseEngine.CreateBuilder("ten-grants");
        builder.Options.RootPath = System.IO.Path.Combine(_root, "ten");
        builder.Options.StatementMemoryBudget = grant;
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE ten(id INT,note TEXT COLLATE binary)");
        int rowBytes = characters == 20 ? 264 : 392;
        int count = multiple * grant / rowBytes;
        for (int first = 0; first < count; first += 200)
        {
            string note = new(threeByte ? '界' : 'x', characters);
            await session.ExecuteAsync("INSERT INTO ten VALUES" + string.Join(',', Enumerable.Range(first, Math.Min(200, count - first)).Select(i => $"({i},'{note}')")));
        }

        // Act / Assert
        if (!succeeds)
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT id,note FROM ten ORDER BY note"));
            failure.Diagnostics[0].Code.ShouldBe("CK05002"); TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        }
        else
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT id,note FROM ten ORDER BY note");
            result.ShouldBeOfType<SqlSpilledResultSet>();
            int visited = 0;
            await foreach (var row in result.GetRowsAsync()) { row.GetInt32(0).ShouldBe(visited++); }
            visited.ShouldBe(count);
            var spill = SqlSortSpillMetrics.For(session.LastStatementMetrics!)!;
            spill.Runs.ShouldBeGreaterThan(1); spill.MergePasses.ShouldBeGreaterThan(0);
            _output.WriteLine($"CAPACITY|chars={characters}|threeByte={threeByte}|multiple={multiple}|rows={visited}|runs={spill.Runs}|passes={spill.MergePasses}|peak={spill.PeakBytes}");
        }
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Spill capacity: a real 64 MiB collector completes ten grants at the default quota (#1789)")]
    public async Task Add_DefaultGrant_ShouldCompleteTenGrantsAndReportTime()
    {
        // Arrange: this avoids millions of durable fixture writes; the real root collector and file-engine quotas run.
        const int grant = 67108864, count = 10 * grant / 264;
        var builder = SqlDatabaseEngine.CreateBuilder("default-grant");
        builder.Options.RootPath = System.IO.Path.Combine(_root, "default");
        await using var engine = await builder.BuildAsync();
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        var order = new[] { new SqlBoundOrdering(SqlBoundConstant.Null, false, true, new SqlBoundCollation(Collation.Binary)) };
        var metrics = new SqlStatementMetrics();
        using var collector = new SqlRowCollector(order, null, null, null, CancellationToken.None, metrics, grant);
        var elapsed = Stopwatch.StartNew();

        // Act
        for (int i = 0; i < count; i++) { string text = new('x', 20); collector.Add([i, text], [text], session); }
        await using var result = collector.TakeSpilled([], 0, -1);
        int visited = 0;
        await foreach (var row in result.GetRowsAsync()) { row.GetInt32(0).ShouldBe(visited++); }
        elapsed.Stop();

        // Assert: timing is diagnostic only; counters establish work and bounded memory.
        visited.ShouldBe(count); metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(grant);
        var spill = SqlSortSpillMetrics.For(metrics)!;
        _output.WriteLine($"DEFAULT_GRANT|rows={count}|seconds={elapsed.Elapsed.TotalSeconds:F3}|runs={spill.Runs}|passes={spill.MergePasses}|peak={spill.PeakBytes}");
    }
}
