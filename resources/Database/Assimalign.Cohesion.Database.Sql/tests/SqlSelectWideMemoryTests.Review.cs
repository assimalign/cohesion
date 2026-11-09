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
    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: wide extrema replace, share, cross the threshold and ignore NULL (#1776)")]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    [InlineData(false, 5)] [InlineData(true, 5)]
    public async Task ExecuteAsync_WideExtremumTransitions_ShouldPinRowsAndBoundaries(bool forceBase, int shape)
    {
        // Arrange
        string[] values = shape == 1 ? [new('c', 2000), new('b', 2000), new('a', 2000)]
            : shape == 2 ? [new('a', 2000), new('b', 3000)] : [new('a', 2000), new('b', 2000), new('c', 2000)];
        string[] setup = shape < 3
            ? ["CREATE TABLE w(k INT,doc TEXT)", "INSERT INTO w VALUES" + string.Join(',', values.Select((value, index) => $"({index},'{value}')"))]
            : ["CREATE TABLE t(k INT,v INT)", "INSERT INTO t VALUES(1,1),(1,2),(2,1),(2,2)" + (shape >= 4 ? ",(1,3),(2,3),(1,NULL),(2,NULL)" : "")];
        var parameters = new Dictionary<string, object?> { ["a"] = new string('a', 200), ["b"] = new string('b', 200) };
        string sql = shape < 3 ? "SELECT MAX(doc) FROM w" : "SELECT k,MAX(CASE WHEN v=1 THEN @a WHEN v=2 THEN " +
            (shape == 4 ? "'z' WHEN v=3 THEN @b ELSE NULL" : "@b ELSE NULL") + " END) FROM t GROUP BY k";
        if (shape == 5) { sql = "SELECT k,MIN(CASE WHEN v=1 THEN 'z' WHEN v=2 THEN @b WHEN v=3 THEN @a ELSE NULL END) FROM t GROUP BY k"; }

        // Act
        var success = await RunWide(setup, sql, parameters, forceBase, 1_048_576);
        var exact = await RunWide(setup, sql, parameters, forceBase, success.Peak);
        var refused = await RunWide(setup, sql, parameters, forceBase, success.Peak - 1);

        // Assert
        success.Code.ShouldBeNull(); exact.Code.ShouldBeNull(); refused.Code.ShouldBe("CS05067");
        success.Values.Count.ShouldBe(shape < 3 ? 1 : 2);
        foreach (var row in success.Values) { row[^1].ShouldBe(shape < 3 ? values.Max(StringComparer.Ordinal) : shape == 4 ? "z" : parameters[shape == 5 ? "a" : "b"]); }
        _output.WriteLine($"EXTREMA_REVIEW|base={forceBase}|shape={shape}|peak={success.Peak}|refused={refused.Peak}");
        long[] peaks = forceBase ? [21168, 21168, 22976, 2656, 2440, 3136] : [8592, 8592, 12592, 2112, 1416, 2112];
        long[] refusals = forceBase ? [17016, 17016, 16824, 2552, 1984, 3032] : [4440, 4440, 6440, 2008, 960, 2008];
        success.Peak.ShouldBe(peaks[shape]);
        refused.Peak.ShouldBe(refusals[shape]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: wide collated DISTINCT representative replacement keeps both owners (#1776)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_WideOrderedRepresentative_ShouldKeepDistinctAndInputOwners(bool forceBase)
    {
        // Arrange
        string[] setup = ["CREATE TABLE t(note TEXT COLLATE case_accent_insensitive)",
            $"INSERT INTO t VALUES('{new string('z', 200)}'),('A{new string('\u0301', 299)}')"];
        const string sql = "SELECT STRING_AGG(DISTINCT spelling(note),',' ORDER BY spelling(note)) FROM t";

        // Act
        var success = await Run(1_048_576);
        var exact = await Run(success.Peak);
        var refused = await Run(success.Peak - 1);

        // Assert
        success.Code.ShouldBeNull(); exact.Code.ShouldBeNull(); refused.Code.ShouldBe("CS05067");
        success.Values.Single().Single().ShouldBe(new string('a', 200));
        success.Peak.ShouldBe(7184); refused.Peak.ShouldBe(5896);
        _output.WriteLine($"WIDE_REPRESENTATIVE|base={forceBase}|peak={success.Peak}|refused={refused.Peak}");

        Task<(long Peak, string? Code, List<object?[]> Values)> Run(long budget)
        {
            int calls = 0;
            return RunWide(setup, sql, null, forceBase, budget,
                SqlScalarFunction.Create("spelling", (string value) => ++calls % 2 == 1 ? new string('a', 200) : value));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: aliasing heap replacement credits only the final holder (#1776)")]
    [InlineData(1, false, 416L)] [InlineData(1, true, 424L)] [InlineData(2, false, 832L)]
    public void Add_AliasingHeapReplacement_ShouldPreserveLiveHolders(int bound, bool repeated, long expected)
    {
        // Arrange
        string a = new('a', 113), b = new('b', 113);
        var metrics = new SqlStatementMetrics();
        using var collector = Collector(metrics, bound, expected);
        collector.Add(repeated ? [b, b] : [b], [b]);
        if (bound == 2) { collector.Add([b], [b]); }
        long initial = metrics.HashMemoryBytes;

        // Act
        collector.Add(repeated ? [a, a] : [a], [a]);

        // Assert
        metrics.HashMemoryBytes.ShouldBe(expected);
        if (bound == 1) { metrics.HashMemoryBytes.ShouldBe(initial); }
        else { collector.Add([a], [a]); metrics.HashMemoryBytes.ShouldBe(initial); }
        collector.Finish().Count.ShouldBe(bound);
        metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: fresh wide top-N values replace the worst input first (#1776)")]
    [InlineData(false)] [InlineData(true)]
    public async Task ExecuteAsync_FreshWideTopN_ShouldRetainBestValues(bool forceBase)
    {
        // Arrange / Act
        var result = await RunWide(["CREATE TABLE t(k INT)", "INSERT INTO t VALUES(5),(4),(3),(2),(1)"],
            "SELECT make(k) AS p FROM t ORDER BY p LIMIT 2", null, forceBase, 1168,
            SqlScalarFunction.Create("make", (long k) => new string((char)('a' + k), 200), SqlFunctionVolatility.Immutable));

        // Assert
        result.Code.ShouldBeNull();
        result.Peak.ShouldBe(1168);
        result.Values.Select(row => row[0]).ShouldBe([new string('b', 200), new string('c', 200)]);
        _output.WriteLine($"FRESH_TOP_N|base={forceBase}|peak={result.Peak}");
    }

    private static async Task<(long Peak, string? Code, List<object?[]> Values)> RunWide(string[] setup, string sql,
        Dictionary<string, object?>? parameters, bool forceBase, long budget, SqlScalarFunction? function = null)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("wide-review");
        builder.Options.StatementMemoryBudget = budget;
        if (function is not null) { builder.Functions.Add(function); }
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string command in setup) { await session.ExecuteAsync(command, cancellationToken: CancellationToken.None); }
        database.Definitions.ForceSelectRetentionBasePath = forceBase;
        string? code = null;
        var values = new List<object?[]>();
        try
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, parameters, CancellationToken.None);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { values.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        }
        catch (DatabaseException failure) { code = failure.Diagnostics[0].Code; }
        Released(session);
        return (session.LastStatementMetrics!.PeakHashMemoryBytes, code, values);
    }

    private static void Released(SqlDatabaseSession session)
    {
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        session.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
    }
}
