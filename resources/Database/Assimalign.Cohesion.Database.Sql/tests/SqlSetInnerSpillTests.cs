using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed partial class SqlSetInnerSpillTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sql-set-inner-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    public SqlSetInnerSpillTests(ITestOutputHelper output) { _output = output; }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }

    private static string Query(int shape) => shape switch
    {
        0 => "SELECT id,k,n FROM t UNION ALL SELECT CAST(id AS BIGINT),k,n FROM t",
        1 => "SELECT id,k,n FROM t UNION SELECT CAST(id AS BIGINT),k,n FROM t",
        2 => "SELECT id/4,k,n FROM t INTERSECT SELECT CAST(id/4 AS BIGINT),k,n FROM t WHERE id%3=0",
        3 => "SELECT id/4,k,n FROM t INTERSECT ALL SELECT CAST(id/4 AS BIGINT),k,n FROM t WHERE id%3=0",
        4 => "SELECT id/4,k,n FROM t EXCEPT SELECT CAST(id/4 AS BIGINT),k,n FROM t WHERE id%3=0",
        5 => "SELECT id/4,k,n FROM t EXCEPT ALL SELECT CAST(id/4 AS BIGINT),k,n FROM t WHERE id%3=0",
        6 => "SELECT DISTINCT id%17 AS k FROM (SELECT id FROM t UNION ALL SELECT id FROM t) d ORDER BY k DESC LIMIT 7 OFFSET 3",
        7 => "CREATE TABLE copied AS SELECT id FROM t UNION ALL SELECT id FROM t ORDER BY id",
        8 => "WITH c AS (SELECT id FROM t UNION ALL SELECT id FROM t) SELECT c.id,o.id FROM o CROSS JOIN c",
        9 => "SELECT o.id,t.id FROM o CROSS JOIN t",
        10 => "SELECT o.id,t.id FROM o JOIN t ON t.id>o.id",
        11 => "SELECT o.id,t.id FROM o JOIN t ON tap(t.id+o.id)>=0",
        12 => "SELECT o.id,t.id FROM o LEFT JOIN t ON t.id<o.id",
        13 => "SELECT t.id,o.id FROM t RIGHT JOIN o ON t.id<o.id",
        14 => "SELECT o.id,t.id FROM o JOIN t ON t.id>o.id WHERE tap(t.id)>=0",
        15 => "SELECT o.id,t.id,p.id FROM (o LEFT JOIN t ON t.id<o.id) LEFT JOIN p ON p.id>t.id",
        16 => "SELECT o.id,t.id,p.id FROM (o CROSS JOIN t) FULL JOIN p ON t.id=p.id",
        17 => "SELECT tap(id),nextval('s') FROM t UNION ALL SELECT tap(id),nextval('s') FROM t",
        18 => "(SELECT id FROM t ORDER BY k) UNION ALL SELECT id FROM t",
        19 => "SELECT o.id,p.id,b.id FROM (o CROSS JOIN p) FULL JOIN t b ON p.id=b.id",
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    [Theory(DisplayName = "Cohesion Test [Sql] - Set operations and spooled inners preserve fitting rows, order, calls and visits over ten grants (#1789)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)]
    public async Task ExecuteAsync_SetAndInnerDifferential_ShouldPreserveResults(int shape)
    {
        // Arrange
        var expected = await ExecuteAsync(shape, 67108864, 134217728, 32768, "reference");

        // Act
        var actual = await ExecuteAsync(shape, 262144, 134217728, 32768, "spilled");

        // Assert
        actual.Code.ShouldBe("ok", actual.Detail);
        actual.Semantics.ShouldBe(expected.Semantics, Query(shape));
        actual.SpillBytes.ShouldBeGreaterThan(0);
        actual.Runs.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Grant and zero-quota sweeps keep every archived base completion (#1789)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)] [InlineData(14)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(18)] [InlineData(19)]
    public async Task ExecuteAsync_GrantSweep_ShouldKeepArchivedCompletions(int shape)
    {
        // Arrange
        long[] grants = [8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576, 2097152, 4194304, 16777216];
        var reference = await ExecuteAsync(shape, 67108864, 134217728, shape == 19 ? 32768 : 1024, "fitting");
        reference.Code.ShouldBe("ok", reference.Detail);
        foreach (long quota in new long[] { 134217728, 0 })
        {
            foreach (long grant in grants)
            {
                // Act
                var actual = await ExecuteAsync(shape, grant, quota, shape == 19 ? 32768 : 1024, $"sweep-{quota}-{grant}");

                // Assert: recorded outcomes are assertions in the companion baseline source.
                _output.WriteLine($"SET_INNER_SWEEP|{shape}|{quota}|{grant}|{actual.Code}|{actual.Semantics}|{actual.Peak}|{actual.SpillBytes}");
                if (Environment.GetEnvironmentVariable("SQ_L4C_RECORD") is { } directory)
                {
                    Directory.CreateDirectory(directory);
                    await File.AppendAllTextAsync(Path.Combine(directory, $"sweep-{shape}.txt"), $"{quota}|{grant}|{actual.Code}|{actual.Semantics}\n");
                }
                else { CheckBaseline(shape, quota, grant, actual, reference); }
                if (actual.Code == "ok") { actual.Semantics.ShouldBe(reference.Semantics, Query(shape)); }
            }
        }
    }

    private async Task<Outcome> ExecuteAsync(int shape, long grant, long quota, int count, string name)
    {
        var calls = new List<long>();
        var builder = SqlDatabaseEngine.CreateBuilder("set-inner");
        builder.Options.RootPath = Path.Combine(_root, name);
        builder.Options.StatementMemoryBudget = grant; builder.Options.StatementTempSpaceQuota = quota;
        builder.Options.CheckpointJournalSize = 65536;
        builder.Functions.Add(SqlScalarFunction.Create("tap", (long value) => { calls.Add(value); return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        await using var session = await (await engine.CreateDatabaseAsync("db")).CreateSessionAsync();
        await SeedAsync(session, count);
        string code = "ok", detail = "";
        var rows = new List<string>();
        long records = 0, pairs = 0, peak = 0, spill = 0, runs = 0;
        try
        {
            var result = await session.ExecuteAsync(Query(shape));
            var metrics = session.LastStatementMetrics!;
            records = metrics.RecordsExamined; pairs = metrics.JoinCandidatePairs; peak = metrics.PeakHashMemoryBytes;
            spill = SqlSortSpillMetrics.For(metrics)?.PeakBytes ?? 0; runs = SqlSortSpillMetrics.For(metrics)?.Runs ?? 0;
            if (result is DatabaseResultSet set)
            {
                await using (set)
                {
                    await foreach (var row in set.GetRowsAsync())
                    { rows.Add(string.Join(';', Enumerable.Range(0, row.FieldCount).Select(i => Cell(row.GetValue(i))))); }
                }
            }
            else
            {
                await using var copied = (DatabaseResultSet)await session.ExecuteAsync("SELECT id FROM copied");
                await foreach (var row in copied.GetRowsAsync()) { rows.Add(Cell(row.GetValue(0))); }
            }
            metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        catch (DatabaseException failure) { code = failure.Diagnostics[0].Code ?? "uncoded"; detail = failure.ToString(); }
        catch (Exception failure) { code = failure.GetType().Name; detail = failure.ToString(); }
        TempBytes(engine.TempSpace!).ShouldBe(0);
        if (Directory.Exists(engine.TempSpace!.DirectoryPath))
        { Directory.EnumerateFiles(engine.TempSpace!.DirectoryPath, "*.ctmp", SearchOption.AllDirectories).ShouldBeEmpty(); }
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0); session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        string semantics = $"{rows.Count},{Hash(rows)},{records},{pairs},{Hash(calls.Select(value => value.ToString(CultureInfo.InvariantCulture)))}";
        return new(code, semantics, peak, spill, runs, detail);
    }

    private static string Cell(object? value) => value is null ? "NULL" : value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
    private static string Hash(IEnumerable<string> values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values))));
    private static async Task SeedAsync(SqlDatabaseSession session, int count)
    {
        await session.ExecuteAsync("CREATE TABLE t(id INT,k INT,n TEXT COLLATE case_insensitive)");
        for (int first = 0; first < count; first += 200)
        {
            await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, Math.Min(200, count - first)).Select(i =>
                $"({i},{(i % 7 == 0 ? "NULL" : (i / 4).ToString(CultureInfo.InvariantCulture))},{(i % 11 == 0 ? "NULL" : i % 2 == 0 ? "'a'" : "'A'")})")));
        }
        await session.ExecuteAsync("CREATE TABLE o(id INT)"); await session.ExecuteAsync("INSERT INTO o VALUES(0),(1),(2)");
        await session.ExecuteAsync("CREATE TABLE p(id INT)"); await session.ExecuteAsync("INSERT INTO p VALUES(0),(1),(32769)");
        await session.ExecuteAsync("CREATE SEQUENCE s");
    }
    private sealed record Outcome(string Code, string Semantics, long Peak, long SpillBytes, long Runs, string Detail);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_bytes")]
    private static extern ref long TempBytes(StorageTempSpace space);
}
