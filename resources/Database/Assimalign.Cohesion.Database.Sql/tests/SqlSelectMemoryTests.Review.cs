using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSelectMemoryTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: normalized output refuses before draining candidates or a later division error (#1776)")]
    [InlineData("RIGHT", false, 6L)] [InlineData("RIGHT", true, 6L)]
    [InlineData("FULL", false, 4L)] [InlineData("FULL", true, 4L)]
    public async Task ExecuteAsync_TreeOutputRefusal_ShouldStopBeforeLaterInputError(string join, bool filtered, long pairs)
    {
        // Arrange: 12,000 B admits both children, then refuses an output row.
        string[] setup = ["CREATE TABLE a(k INT)", "INSERT INTO a VALUES" + string.Join(',', Enumerable.Range(1, 100).Select(k => $"({k})")),
            "CREATE TABLE b(k INT)", "INSERT INTO b VALUES(1),(2),(3),(4),(5),(6),(7),(8)"];
        string sql = $"SELECT a.k,b.k FROM a {join} JOIN b ON a.k" + (join == "FULL" ? "=b.k" : ">=b.k") +
            (filtered ? " WHERE 1000000/(100-a.k)>0" : "");

        // Act
        var result = await Probe(setup, sql, 12_000);

        // Assert
        _output.WriteLine($"TREE_OUTPUT_REFUSAL|{join}|filtered={filtered}|peak={result.Peak}|records={result.Records}|pairs={result.Pairs}|code={result.Code}");
        result.Code.ShouldBe("CS05067");
        result.Pairs.ShouldBe(pairs);
        result.Pairs.ShouldBeGreaterThan(0);
        result.Pairs.ShouldBeLessThan(800);
        result.Peak.ShouldBe(join == "RIGHT" ? 11912 : 11896);
        result.Records.ShouldBe(108);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: shared wide hash builds still charge each reference (#1776)")]
    [InlineData(30, false)] [InlineData(31, true)]
    public async Task ExecuteAsync_SharedWideHashBuild_ShouldPinPerReferenceBoundary(int count, bool refuses)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("hash-reference-boundary", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[] { "CREATE TABLE a(k INT)", "INSERT INTO a VALUES(1)", "CREATE TABLE t(k INT)",
            "INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(1, count).Select(k => $"({k})")) })
        { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }
        var parameters = new Dictionary<string, object?> { ["p"] = new string('x', 1_048_576) };
        const string query = "SELECT a.k FROM a JOIN (SELECT @p AS p,k FROM t) d ON a.k=d.k";

        // Act
        async Task Read()
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, parameters, CancellationToken.None);
            int rows = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(1); rows++; }
            rows.ShouldBe(1);
        }
        if (refuses) { (await Should.ThrowAsync<DatabaseException>(Read)).Diagnostics[0].Code.ShouldBe("CS05067"); }
        else { await Read(); }

        // Assert
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        metrics.JoinAccessPaths.ShouldBe(["join-hash"]);
        metrics.PeakHashMemoryBytes.ShouldBe(refuses ? 65018696 : 65018688);
        metrics.RecordsExamined.ShouldBe(count + 1);
        metrics.HashMemoryBytes.ShouldBe(0); metrics.PlainSelectMemoryBytes.ShouldBe(0);
        _output.WriteLine($"HASH_REFERENCE_BOUNDARY|rows={count}|refuses={refuses}|peak={metrics.PeakHashMemoryBytes}|records={metrics.RecordsExamined}");
    }

#if !DEBUG
    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT memory: narrow extrema allocate no identity-owner array (#1776)")]
    [InlineData("SELECT MAX(name) FROM t", 651520L)]
    [InlineData("SELECT k,(SELECT MAX(name) FROM u WHERE u.k=t.k) FROM t", 8091776L)]
    public async Task ExecuteAsync_NarrowExtremum_ShouldAvoidOwnerArrayAllocation(string sql, long maximum)
    {
        // Arrange: all cells remain below the wide threshold, with 100 correlated invocations.
        // d1c879cc4 used 652,672 / 8,206,976 thread bytes per 16 requests. Removing
        // a dead 72-byte owner array per invocation gives these caps on arm64 and x64.
        await using var engine = SqlDatabaseEngine.Create("narrow-extremum-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string command in new[] { "CREATE TABLE t(k INT,name TEXT)", "CREATE TABLE u(k INT,name TEXT)",
            "INSERT INTO t VALUES" + string.Join(',', Enumerable.Repeat("(1,'alpha')", 100)), "INSERT INTO u VALUES(1,'alpha'),(1,'omega')" })
        { await session.ExecuteAsync(command, cancellationToken: CancellationToken.None); }
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = SqlQueryRequest.FromSql(sql);
        Measure(32);

        // Act
        long minimum = long.MaxValue;
        for (int round = 0; round < 3; round++) { minimum = Math.Min(minimum, Measure(16)); }

        // Assert
        _output.WriteLine($"NARROW_EXTREMUM_ALLOCATION|{sql}|thread-bytes/16={minimum}|per-query={minimum / 16d}|cap={maximum}");
        minimum.ShouldBeLessThanOrEqualTo(maximum);

        long Measure(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                pending.IsCompletedSuccessfully.ShouldBeTrue();
                var rows = (DatabaseResultSet)pending.Result;
                rows.DisposeAsync().GetAwaiter().GetResult();
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
#endif
}
