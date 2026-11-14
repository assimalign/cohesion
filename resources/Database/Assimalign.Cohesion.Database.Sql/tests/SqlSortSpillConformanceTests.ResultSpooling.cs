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
    [Theory(DisplayName = "Cohesion Test [Sql] - Result spooling grant sweep preserves rows, order, calls, sequences and work (#1789)")]
    [InlineData("root")]
    [InlineData("index")]
    [InlineData("ctas")]
    [InlineData("copy")]
    [InlineData("insert")]
    [InlineData("nested")]
    [InlineData("group")]
    [InlineData("root", true)]
    [InlineData("index", true)]
    [InlineData("ctas", true)]
    [InlineData("copy", true)]
    [InlineData("insert", true)]
    [InlineData("nested", true)]
    [InlineData("group", true)]
    public async Task ExecuteAsync_ResultGrantSweep_ShouldPreserveCapability(string shape, bool quotaZero = false)
    {
        // Arrange: the optional recording directory lets the same test run against the
        // batch base and the implementation, point for point, without changing a plan.
        long[] grants = shape == "group"
            ? [131072, 196608, 262144, 1048576, 4184396, 4223999, 4224000, 4224001, 4230000, 4329158, 4500000, 4800000, 5200000, 5600000, 5776774, 6000000, 8388608]
            : [4096, 65536, 131072, 196608, 262144, 524288, 1048576, 4194304, 8388608];
        string? record = Environment.GetEnvironmentVariable("SQ_L4A_RECORD");
        string? baseline = Environment.GetEnvironmentVariable("SQ_L4A_BASELINE");
        var expected = await ResultProbeAsync(shape, 67108864, "expected");
        expected.Code.ShouldBe("ok");

        // Act / Assert
        foreach (long grant in grants)
        {
            var actual = await ResultProbeAsync(shape, grant, "grant-" + grant, quotaZero ? 0 : 67108864);
            _output.WriteLine($"RESULT_SWEEP|{shape}|quotaZero={quotaZero}|G={grant}|{actual}");
            if (record is not null)
            {
                string destination = quotaZero ? Path.Combine(record, "quota-zero") : record;
                Directory.CreateDirectory(destination);
                await File.WriteAllLinesAsync(Path.Combine(destination, shape + "-" + grant + ".txt"), actual.Lines());
            }
            if (actual.Code == "ok")
            {
                actual.Digest.ShouldBe(expected.Digest);
                actual.Calls.ShouldBe(expected.Calls);
                actual.Sequence.ShouldBe(expected.Sequence);
                actual.Examined.ShouldBe(expected.Examined);
            }
            else if (quotaZero)
            { new[] { "CS05067", "CK05002" }.ShouldContain(actual.Code); }
            else { actual.Code.ShouldBe("CS05067"); }
            if (baseline is not null)
            {
                string profile = quotaZero ? Path.Combine(baseline, "quota-zero") : baseline;
                var before = await File.ReadAllLinesAsync(Path.Combine(profile, shape + "-" + grant + ".txt"));
                if (before[0] == "ok") { actual.Lines().Take(5).ShouldBe(before.Take(5)); }
                if (before[0] == "ok" && before[5] == "0" && actual.Runs == 0)
                { actual.Peak.ShouldBe(long.Parse(before[6], System.Globalization.CultureInfo.InvariantCulture)); }
            }
            if (!quotaZero && record is null && grant >= (shape == "group" ? 196608 : 262144))
            { actual.Code.ShouldBe("ok", customMessage: shape + " at " + grant); }
            // These 22 base completions used no temporary pages. Six of the 28
            // regular-quota completions already spilled on the base.
            bool baseCompleted = shape switch
            {
                "insert" => true,
                "group" => grant is 6000000 or 8388608,
                "copy" => grant is 524288 or 1048576 or 4194304 or 8388608,
                "nested" => grant == 8388608,
                _ => grant >= 4194304,
            };
            if (quotaZero && record is null && baseCompleted) { actual.Code.ShouldBe("ok"); }
        }
    }

    private async Task<ResultProbe> ResultProbeAsync(string shape, long grant, string name, long quota = 67108864)
    {
        var calls = new List<long>();
        await using var engine = await Create(shape + "-" + name, grant, calls, quota);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,k INT,note TEXT)");
        int count = shape is "group" or "nested" ? 12000 : shape == "copy" ? 100 : 1600;
        for (int first = 0; first < count; first += 200)
        {
            await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, Math.Min(200, count - first))
                .Select(i => $"({i},{i % 79},'{new string('x', shape == "group" ? 1 : 1024)}')")));
        }
        await session.ExecuteAsync("CREATE SEQUENCE s");
        if (shape == "insert") { await session.ExecuteAsync("CREATE TABLE target(id INT,k INT,note TEXT)"); }
        string sql = shape switch
        {
            "root" => "SELECT stamp(id),nextval('s'),note FROM t",
            "index" => "SELECT id,k,note FROM t WHERE id>=0 ORDER BY id",
            "ctas" => "CREATE TABLE target AS SELECT id,k,note FROM t",
            "copy" => "CREATE TABLE target AS SELECT id,k,note FROM t ORDER BY k,id",
            "insert" => "INSERT INTO target SELECT id,k,note FROM t",
            "nested" => "SELECT COUNT(*) FROM (SELECT id,MIN(k) AS n FROM t GROUP BY id) d",
            _ => "SELECT id,MIN(k) FROM t GROUP BY id",
        };
        string code = "ok", digest = "";
        long examined = 0, runs = 0, peak = 0;
        DatabaseResult? result = null;
        try
        {
            result = await session.ExecuteAsync(sql);
            var metrics = session.LastStatementMetrics!;
            examined = metrics.RecordsExamined; peak = metrics.PeakHashMemoryBytes;
            runs = SqlSortSpillMetrics.For(metrics)?.Runs ?? 0;
            metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
            if (result is not DatabaseResultSet) { result.AffectedCount.ShouldBe(count); }
            var rows = result is DatabaseResultSet set ? await Rows(set) : await ResultProbeRowsAsync(session, grant);
            digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows.Select(row => row.ToString())))));
            peak.ShouldBeLessThanOrEqualTo(grant);
        }
        catch (DatabaseException failure) when (result is null or DatabaseResultSet)
        {
            code = failure.Diagnostics[0].Code!;
            var metrics = session.LastStatementMetrics!;
            examined = metrics.RecordsExamined; peak = metrics.PeakHashMemoryBytes;
            runs = SqlSortSpillMetrics.For(metrics)?.Runs ?? 0;
            metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        finally { if (result is DatabaseResultSet disposable) { await disposable.DisposeAsync(); } }
        long sequence = 0;
        if (shape == "root" && calls.Count > 0)
        {
            await using var current = (DatabaseResultSet)await session.ExecuteAsync("SELECT currval('s')");
            sequence = Convert.ToInt64((await Rows(current))[0].Cells[0]);
        }
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
        return new(code, digest, string.Join(',', calls), sequence, examined, runs, peak);
    }

    private static async Task<RowValues[]> ResultProbeRowsAsync(SqlDatabaseSession session, long grant)
    {
        int size = (int)Math.Max(1, Math.Min(64, grant / 4096));
        var rows = new List<RowValues>();
        for (int offset = 0; ; offset += size)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync($"SELECT * FROM target LIMIT {size} OFFSET {offset}");
            var page = await Rows(result); rows.AddRange(page);
            if (page.Length < size) { return rows.ToArray(); }
        }
    }

    private sealed record ResultProbe(string Code, string Digest, string Calls, long Sequence, long Examined, long Runs, long Peak)
    {
        internal string[] Lines() => [Code, Digest, Calls, Sequence.ToString(), Examined.ToString(), Runs.ToString(), Peak.ToString()];
        public override string ToString() => $"code={Code}|digest={Digest}|calls={Calls.Split(',').Length}|sequence={Sequence}|examined={Examined}|runs={Runs}|peak={Peak}";
    }
}
