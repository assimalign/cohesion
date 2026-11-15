using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - A staged unordered join reports its source run even when final output fits or is empty (#1789)")]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ExecuteAsync_StagedUnorderedExplain_ShouldReportSourceRun(int limit)
    {
        // Arrange
        await using var engine = await Create("output-source-explain", 262144);
        var database = await engine.CreateDatabaseAsync("db");
        database.Definitions.ForceSelectRetentionBasePath = true;
        await using var session = await database.CreateSessionAsync(); await Seed(session);

        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(
            $"EXPLAIN ANALYZE SELECT nextval('s'),t.id FROM t CROSS JOIN u LIMIT {limit}");
        string description = string.Join('\n', (await Rows(result)).SelectMany(row => row.Cells.OfType<string>()));

        // Assert
        description.ShouldContain("spill_runs=1", Case.Sensitive);
        description.ShouldContain("spilled_bytes=", Case.Sensitive);
        description.ShouldNotContain("hash_spilled_bytes=", Case.Sensitive);
        var metrics = session.LastStatementMetrics!;
        SqlSortSpillMetrics.For(metrics).ShouldNotBeNull().PeakBytes.ShouldBeGreaterThan(0);
        metrics.RecordsExamined.ShouldBe(4001); metrics.HashMemoryBytes.ShouldBe(0);
        TempBytes(engine.TempSpace!).ShouldBe(0); Files(engine).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Unordered output differential preserves exact cells, pagination, nested sources and volatile boundaries (#1789)")]
    public async Task ExecuteAsync_UnorderedOutputCorpus_ShouldMatchFittingRowsAndEvaluation()
    {
        // Arrange
        var calls = new[] { new List<long>(), new List<long>() };
        await using var fitting = await Create("output-corpus-fit", 67108864, calls[0]);
        await using var spilling = await Create("output-corpus-spill", 262144, calls[1]);
        await using var a = await (await fitting.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilling.CreateDatabaseAsync("db")).CreateSessionAsync();
        a.Database.Definitions.ForceSelectRetentionBasePath = true;
        await Seed(a); await Seed(b);
        string[] queries =
        [
            "SELECT id,k,note FROM t",
            "SELECT id,k,note FROM t LIMIT 3000 OFFSET 17",
            "SELECT id,stamp(id),RPAD(note,1024,'x') FROM t",
            "SELECT id,nextval('s') AS n,CAST(note AS CHAR(10)) FROM t",
            "SELECT id,CAST(id AS DECIMAL(10,2)),CAST(id AS REAL),CAST(id AS DOUBLE PRECISION),X'00FF' FROM t",
            "SELECT id,DATE '2024-01-02',TIME '12:34:56',TIMESTAMP '2024-01-02 12:34:56',TIMESTAMP WITH TIME ZONE '2024-01-02 12:34:56+03:00' FROM t",
            "SELECT id,CAST('{\"x\":1}' AS JSON),CAST('{\"x\":1}' AS JSONB) FROM t",
            "SELECT stamp(t.id),RPAD(t.note,1024,'x') FROM t CROSS JOIN u LIMIT 3000 OFFSET 17",
            "SELECT nextval('s'),t.id,RPAD(t.note,1024,'x') FROM t CROSS JOIN u LIMIT 3000 OFFSET 17",
            "SELECT id,MIN(k),CAST(id AS CHAR(10)),X'00FF' FROM t GROUP BY id LIMIT 3000 OFFSET 17",
            "SELECT DISTINCT RPAD(CASE WHEN id%80%2=0 THEN 'a' ELSE 'A' END,2000,'x') COLLATE case_insensitive AS note FROM t GROUP BY id%80 LIMIT 1 OFFSET 1",
            "WITH d AS (SELECT id,MIN(k) AS k FROM t GROUP BY id) SELECT * FROM d",
            "SELECT * FROM (SELECT id,MIN(k) AS k FROM t GROUP BY id) d",
            "SELECT * FROM (SELECT id,k FROM t ORDER BY k LIMIT 3000 OFFSET 17) d",
            "WITH d AS (SELECT id,k FROM t ORDER BY k LIMIT 3000 OFFSET 17) SELECT * FROM d",
            "WITH d AS (SELECT id,CAST(note AS CHAR(10)) AS c,X'00FF' AS raw FROM t) SELECT id,c,raw FROM d",
        ];

        // Act / Assert
        foreach (string sql in queries)
        {
            calls[0].Clear(); calls[1].Clear();
            await using var expected = (DatabaseResultSet)await a.ExecuteAsync(sql);
            await using var actual = (DatabaseResultSet)await b.ExecuteAsync(sql);
            (await Rows(actual)).ShouldBe(await Rows(expected), customMessage: sql);
            actual.Columns.Select(c => (c.Name, c.Ordinal, c.Type, c.MaxLength, c.IsNullable)).ShouldBe(
                expected.Columns.Select(c => (c.Name, c.Ordinal, c.Type, c.MaxLength, c.IsNullable)));
            calls[1].ShouldBe(calls[0], customMessage: sql);
            b.LastStatementMetrics!.RecordsExamined.ShouldBe(a.LastStatementMetrics!.RecordsExamined);
            b.LastStatementMetrics.HashMemoryBytes.ShouldBe(0); b.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            b.LastStatementMetrics.PeakHashMemoryBytes.ShouldBeLessThanOrEqualTo(262144);
            SqlSortSpillMetrics.For(b.LastStatementMetrics).ShouldNotBeNull().Runs.ShouldBeGreaterThan(0);
            await actual.DisposeAsync(); TempBytes(spilling.TempSpace!).ShouldBe(0); Files(spilling).ShouldBe(0);
            _output.WriteLine($"OUTPUT_CORPUS|{sql}");
        }
        await using var expectedSequence = (DatabaseResultSet)await a.ExecuteAsync("SELECT currval('s')");
        await using var actualSequence = (DatabaseResultSet)await b.ExecuteAsync("SELECT currval('s')");
        (await Rows(actualSequence)).ShouldBe(await Rows(expectedSequence));
    }
}
