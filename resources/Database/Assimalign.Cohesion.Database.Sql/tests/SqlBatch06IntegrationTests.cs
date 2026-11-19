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
public sealed partial class SqlBatch06IntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sql-batch06-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public SqlBatch06IntegrationTests(ITestOutputHelper output) { _output = output; }
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }

    private static readonly long[] _grants = [1, 1024, 8192, 32768, 65536, 131072, 262144, 524288, 1048576, 1572864, 2097152, 3145728, 4194304, 16777216, 33554432];

    private static string Query(int shape) => shape switch
    {
        0 => "SELECT id,tag,n,RPAD(CAST(id AS TEXT),256,'x') AS wide FROM (SELECT DISTINCT id,tag,ROW_NUMBER() OVER(PARTITION BY id%32 ORDER BY tag,id) AS n FROM (SELECT tap(t.id) AS id,nextval('s') AS tag FROM o CROSS JOIN t UNION ALL SELECT tap(id),nextval('s') AS tag FROM t) u) w ORDER BY id DESC,tag LIMIT 2700",
        1 => "WITH c AS (SELECT tap(id) AS id FROM t UNION ALL SELECT tap(id) FROM t) SELECT c.id,o.id AS tag,ROW_NUMBER() OVER(PARTITION BY c.id%32 ORDER BY c.id,o.id) AS n FROM o CROSS JOIN c UNION ALL SELECT id,3 AS tag,ROW_NUMBER() OVER(PARTITION BY id%32 ORDER BY id) AS n FROM c",
        2 => "CREATE TABLE copied AS SELECT id,ROW_NUMBER() OVER(PARTITION BY id%32 ORDER BY id) AS n,tap(id) AS v,nextval('s') AS seq FROM (SELECT id FROM t UNION ALL SELECT id FROM t) u",
        3 => "INSERT INTO sink SELECT tap(id),nextval('s') FROM t UNION ALL SELECT tap(id),nextval('s') FROM t",
        4 => "INSERT INTO sink SELECT tap(t.id),nextval('s') FROM o CROSS JOIN t",
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    [Theory(DisplayName = "Cohesion Test [Sql] - Batch 6 combined spill chains preserve archived batch-5 completions and refusals (#1789, #1828)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task ExecuteAsync_CombinedSpillSweep_ShouldKeepArchivedCapability(int shape)
    {
        // Arrange: the companion pins are measured from an independent ed48f2f61 build.
        var fitting = await ExecuteAsync(shape, 16777216, false, "");
        fitting.Code.ShouldBe("ok", fitting.Detail);
        foreach (bool zeroQuota in new[] { false, true })
        {
            foreach (long grant in _grants)
            {
                // Act
                var actual = await ExecuteAsync(shape, grant, zeroQuota, "");
                string key = $"sweep-{shape}-{zeroQuota}-{grant}";
                CheckArchived(key, actual);

                // Assert: completion includes row order, CLR values, metadata, calls, sequence and visits.
                if (actual.Code == "ok") { actual.Semantics.ShouldBe(fitting.Semantics, key); }
                if (!zeroQuota && (shape == 0 && grant == 2097152 || shape == 1 && grant == 262144))
                {
                    actual.ResultType.ShouldBe(nameof(SqlSpilledResultSet));
                    actual.SpillBytes.ShouldBeGreaterThan(0);
                }
                if (!zeroQuota && shape == 2 && grant == 1048576) { actual.SpillBytes.ShouldBeGreaterThan(0); }
                if (shape is 3 or 4) { actual.SpillBytes.ShouldBe(0); }
                _output.WriteLine($"BATCH06|{key}|{actual.Code}|{actual.Semantics}|spill={actual.SpillBytes}|result={actual.ResultType}");
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Combined EXPLAIN text and SELECT-only ANALYZE policy match archived batch 5 (#1789)")]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task ExecuteAsync_CombinedExplain_ShouldKeepArchivedText(int shape)
    {
        // Arrange / Act
        foreach (string prefix in new[] { "EXPLAIN ", "EXPLAIN ANALYZE " })
        {
            var actual = await ExecuteAsync(shape, 16777216, false, prefix);
            // Assert
            CheckArchived($"explain-{shape}-{prefix.Trim().Replace(' ', '-')}", actual);
            _output.WriteLine($"BATCH06_EXPLAIN|{shape}|{prefix}|{actual.Code}|{actual.Semantics}|{actual.Detail}");
        }
    }

    private async Task<Outcome> ExecuteAsync(int shape, long grant, bool zeroQuota, string prefix)
    {
        var calls = new List<long>();
        var builder = SqlDatabaseEngine.CreateBuilder("batch06");
        builder.Options.TempDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        builder.Options.StatementMemoryBudget = grant;
        if (zeroQuota) { builder.Options.StatementTempSpaceQuota = 0; }
        builder.Functions.Add(SqlScalarFunction.Create("tap", (long value) => { calls.Add(value); return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT)");
        for (int first = 0; first < 1024; first += 128)
        { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(first, 128).Select(i => $"({i})"))); }
        await session.ExecuteAsync("CREATE TABLE o(id INT)"); await session.ExecuteAsync("INSERT INTO o VALUES(0),(1)");
        await session.ExecuteAsync("CREATE TABLE sink(id BIGINT,seq BIGINT)"); await session.ExecuteAsync("CREATE SEQUENCE s");
        string code = "ok", detail = "", metadata = "", resultType = "";
        var rows = new List<string>();
        long records = 0, pairs = 0, spill = 0, affected = -1;
        try
        {
            var result = await session.ExecuteAsync(prefix + Query(shape), cancellationToken: CancellationToken.None);
            resultType = result.GetType().Name; affected = result.AffectedCount;
            var metrics = session.LastStatementMetrics!;
            records = metrics.RecordsExamined; pairs = metrics.JoinCandidatePairs;
            spill = SqlSortSpillMetrics.For(metrics)?.PeakBytes ?? 0;
            if (result is DatabaseResultSet set) { await ReadAsync(set); }
            metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
        }
        catch (DatabaseException failure)
        {
            code = failure.Diagnostics.First().Code ?? "uncoded"; detail = failure.Message;
            records = session.LastStatementMetrics?.RecordsExamined ?? 0;
            pairs = session.LastStatementMetrics?.JoinCandidatePairs ?? 0;
        }
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
        // Observe the committed physical insertion order after recording statement counters.
        // This read has no SELECT grant, so even a one-byte DML grant can be compared.
        if (prefix.Length == 0 && code != "ok" && shape == 2)
        { database.Catalog.TryGetTable("dbo", "copied", out _).ShouldBeFalse(); }
        if (prefix.Length == 0 && code == "ok" && shape >= 2)
        {
            database.Catalog.TryGetTable("dbo", shape == 2 ? "copied" : "sink", out var table).ShouldBeTrue();
            metadata = string.Join(';', table.Columns.Select((c, i) => $"{i}:{c.Name}:{c.Type.Type}:{c.Type.MaxLength}:{c.IsNullable}"));
            using var units = database.DataStorage.GetUnitIterator(table.ObjectId);
            while (units.MoveNext())
            {
                var cells = SqlRowCodec.Decode(units.Current.Data.Span, table, out _, out _, out _);
                rows.Add(string.Join(';', cells.Select(Cell)));
            }
        }
        string sequence = session.SequenceState.Last == 0 ? "none" : Cell(session.SequenceState.Values[session.SequenceState.Last]);
        TempBytes(engine.TempSpace!).ShouldBe(0);
        if (Directory.Exists(engine.TempSpace!.DirectoryPath))
        { Directory.EnumerateFiles(engine.TempSpace!.DirectoryPath, "*.ctmp", SearchOption.AllDirectories).ShouldBeEmpty(); }
        if (prefix.Length > 0 && code == "ok") { detail = string.Join('\n', rows); }
        string semantics = $"{rows.Count},{Hash(rows)},{Hash([metadata])},{calls.Count},{Hash(calls.Select(v => v.ToString(CultureInfo.InvariantCulture)))},{sequence},{records},{pairs},{affected}";
        return new(code, semantics, calls.Count, records, spill, resultType, detail);

        async Task ReadAsync(DatabaseResultSet set)
        {
            await using (set)
            {
                metadata = string.Join(';', set.Columns.Select(c => $"{c.Ordinal}:{c.Name}:{c.Type}:{c.MaxLength}:{c.IsNullable}"));
                await foreach (var row in set.GetRowsAsync())
                { rows.Add(string.Join(';', Enumerable.Range(0, row.FieldCount).Select(i => Cell(row.GetValue(i))))); }
            }
        }
    }

    private static string Cell(object? value) => value is null ? "NULL" : value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
    private static string Hash(IEnumerable<string> values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values))));
    private sealed record Outcome(string Code, string Semantics, int Calls, long Records, long SpillBytes, string ResultType, string Detail);
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_bytes")]
    private static extern ref long TempBytes(StorageTempSpace space);
}
