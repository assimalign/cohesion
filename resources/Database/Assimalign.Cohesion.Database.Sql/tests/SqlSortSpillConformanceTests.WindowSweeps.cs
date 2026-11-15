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
    [Theory(DisplayName = "Cohesion Test [Sql] - Derived, view and membership window corpus retains the recorded base results (#1789)")]
    [InlineData(16, "15FDD7A4851D14BB6EEE28831F3458FC9FFAA91761786444C07764C0BE77305B", 400, 2000)]
    [InlineData(17, "15FDD7A4851D14BB6EEE28831F3458FC9FFAA91761786444C07764C0BE77305B", 400, 2000)]
    [InlineData(18, "39913745D012B5A63057006758F14E0CEA4C747F789306A17EABD2251B445986", 0, 3200)]
    [InlineData(19, "3C40BECB1E1D494D2D48502B443438E4A9551F4AB839A2F51254CFB88F9FA70A", 0, 3608)]
    public async Task ExecuteAsync_WindowReviewCorpus_ShouldMatchRecordedBase(int shape, string digest, long sequence, long examined)
    {
        // Arrange: exact success pins recorded from 9973a4e36, independent of dispatch switches.
        foreach (long grant in new long[] { 417792, 524288, 2097152, 67108864 })
        {
            // Act
            var actual = await WindowProbeAsync(shape, grant, false, false);
            // Assert: the grant sweep separately checks every refusal against that binary.
            if (grant == 67108864) { actual.Code.ShouldBe("ok"); }
            if (actual.Code != "ok") { continue; }
            actual.Digest.ShouldBe(digest); actual.Sequence.ShouldBe(sequence);
            actual.Examined.ShouldBe(examined); actual.Calls.ShouldBeEmpty();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window grants and quota zero preserve every base completion and the original refusal code (#1789)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)]
    [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)]
    public async Task ExecuteAsync_WindowGrantSweep_ShouldPreserveCapability(int shape)
    {
        // Arrange: the fitting peak supplies the exact boundary and both neighbours.
        // Recording also runs unchanged against the batch-base binary, without a switch.
        var expected = await WindowProbeAsync(shape, 67108864, false, false);
        expected.Code.ShouldBe("ok");
        long[] grants = [4096, 65536, 131072, 196608, 262144, 393216, 524288, 786432,
            1048576, 2097152, 4194304, 8388608, expected.Peak - 1, expected.Peak, expected.Peak + 1];
        if (shape is 16 or 17 or 18)
        { grants = grants.Concat(Enumerable.Range(0, 17).Select(index => 393216L + 8192L * index)).ToArray(); }
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
        var database = await engine.CreateDatabaseAsync("db");
        // The general retention switch also changes independent membership admission.
        // That shape uses the separately recorded 9973a4e36 execution as its reference.
        database.Definitions.ForceSelectRetentionBasePath = forceBase && shape != 19;
        await using var session = await database.CreateSessionAsync(); await Seed(session, 1600);
        if (shape >= 16) { await SeedWindowReviewAsync(session); }
        if (shape == 19) { await session.ExecuteAsync("INSERT INTO u VALUES(2),(3),(4),(5),(6),(7),(8)"); }
        string sql = WindowSweepSql(shape);
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
        if (shape is 8 or 9 or 16 or 17)
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

    private static string WindowSweepSql(int shape) => shape switch
    {
        15 => "CREATE TABLE target AS SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY k) AS n FROM t",
        16 => "SELECT d.id,ROW_NUMBER() OVER(PARTITION BY d.id%10 ORDER BY nextval('s')) FROM (SELECT id,note FROM t) d JOIN small ON small.id=d.id",
        17 => "SELECT d.id,ROW_NUMBER() OVER(PARTITION BY d.id%10 ORDER BY nextval('s')) FROM window_input d JOIN small ON small.id=d.id",
        18 => "SELECT d.id,STRING_AGG(d.note,',') OVER(PARTITION BY 0 ORDER BY d.id) FROM (SELECT id,note FROM t) d JOIN t small ON small.id=d.id AND small.id<1200",
        19 => "SELECT d.id,ROW_NUMBER() OVER(PARTITION BY d.id%100 ORDER BY d.id) FROM (SELECT id FROM t WHERE id IN (SELECT id FROM small)) d CROSS JOIN u WHERE d.id>(SELECT MIN(id) FROM t)",
        _ => _windowCorpus[shape],
    };

    private static async Task SeedWindowReviewAsync(SqlDatabaseSession session)
    {
        await session.ExecuteAsync("CREATE TABLE small(id INT)");
        for (int first = 0; first < 400; first += 200)
        { await session.ExecuteAsync("INSERT INTO small VALUES" + string.Join(',', Enumerable.Range(first, 200).Select(i => $"({i})"))); }
        await session.ExecuteAsync("CREATE VIEW window_input AS SELECT id,note FROM t");
    }
}
