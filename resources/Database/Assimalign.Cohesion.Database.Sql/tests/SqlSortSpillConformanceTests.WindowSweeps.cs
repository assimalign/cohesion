using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Window grants and quota zero preserve every base completion and the original refusal code (#1789)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    public async Task ExecuteAsync_WindowGrantSweep_ShouldPreserveCapability(int shape)
    {
        // Arrange: the fitting peak supplies the exact boundary and both neighbours.
        // Recording also runs unchanged against the batch-base binary, without a switch.
        var expected = await WindowProbeAsync(shape, 67108864, false, false);
        expected.Code.ShouldBe("ok");
        long[] grants = [4096, 65536, 131072, 196608, 262144, 393216, 524288, 786432,
            1048576, 2097152, 4194304, 8388608, expected.Peak - 1, expected.Peak, expected.Peak + 1];
        string? record = Environment.GetEnvironmentVariable("SQ_L4B_RECORD");
        string? baseline = Environment.GetEnvironmentVariable("SQ_L4B_BASELINE");
        foreach (bool zero in new[] { false, true })
        {
        foreach (long grant in grants.Distinct().Order())
        {
            var before = await WindowProbeAsync(shape, grant, true, zero);
            // Act
            var after = await WindowProbeAsync(shape, grant, false, zero);
            // Assert
            _output.WriteLine($"WINDOW_SWEEP|shape={shape}|quotaZero={zero}|G={grant}|base={before.Code}|head={after.Code}|peak={after.Peak}|runs={after.Runs}");
            if (after.Code == "ok")
            { after.Lines().Skip(1).Take(4).ShouldBe(expected.Lines().Skip(1).Take(4)); }
            if (before.Code == "ok") { after.Lines().Take(5).ShouldBe(before.Lines().Take(5)); }
            else if (after.Code != "ok") { after.Code.ShouldBe(before.Code); }
            if (before.Code == "ok" && before.Runs == 0 && after.Runs == 0) { after.Peak.ShouldBe(before.Peak); }
            string file = $"{shape}-{zero}-{grant}.txt";
            if (record is not null) { Directory.CreateDirectory(record); await File.WriteAllLinesAsync(Path.Combine(record, file), after.Lines()); }
            if (baseline is not null)
            {
                var original = await File.ReadAllLinesAsync(Path.Combine(baseline, file));
                if (original[0] == "ok") { after.Lines().Take(5).ShouldBe(original.Take(5)); }
                else if (after.Code != "ok") { after.Code.ShouldBe(original[0]); }
            }
        }
        }
    }

    private async Task<ResultProbe> WindowProbeAsync(int shape, long grant, bool forceBase, bool quotaZero)
    {
        var calls = new List<long>();
        string name = $"window-probe-{shape}-{grant}-{forceBase}-{quotaZero}";
        await using var engine = await CreateWindowAsync(name, grant, calls, quotaZero ? 0 : 134217728);
        var database = await engine.CreateDatabaseAsync("db"); database.Definitions.ForceSelectRetentionBasePath = forceBase;
        await using var session = await database.CreateSessionAsync(); await Seed(session, 1600);
        string sql = shape == 15 ? "CREATE TABLE target AS SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY k) AS n FROM t" : _windowCorpus[shape];
        string code = "ok", digest = "";
        long examined = 0, peak = 0, runs = 0, sequence = 0;
        DatabaseResultSet? result = null;
        try
        {
            var executed = await session.ExecuteAsync(sql); result = executed as DatabaseResultSet;
            examined = session.LastStatementMetrics!.RecordsExamined; peak = session.LastStatementMetrics.PeakHashMemoryBytes;
            runs = SqlHashSpillMetrics.For(session.LastStatementMetrics)?.Runs ?? SqlSortSpillMetrics.For(session.LastStatementMetrics)?.Runs ?? 0;
            var rows = result is not null ? await Rows(result) : await ResultProbeRowsAsync(session, grant);
            digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows.Select(row => row.ToString())))));
        }
        catch (DatabaseException failure)
        {
            code = failure.Diagnostics[0].Code!; examined = session.LastStatementMetrics!.RecordsExamined;
            peak = session.LastStatementMetrics.PeakHashMemoryBytes;
        }
        finally { if (result is not null) { await result.DisposeAsync(); } }
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        if (shape is 8 or 9)
        {
            try
            {
                await using var current = (DatabaseResultSet)await session.ExecuteAsync("SELECT currval('s')");
                sequence = Convert.ToInt64((await Rows(current))[0].Cells[0]);
            }
            catch (DatabaseException) { }
        }
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        return new(code, digest, string.Join(',', calls), sequence, examined, runs, peak);
    }
}
