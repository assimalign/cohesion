using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSortSpillConformanceTests
{
    private static readonly string[] _windowCorpus =
    [
        "SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY k),RANK() OVER(PARTITION BY id%100 ORDER BY k),DENSE_RANK() OVER(PARTITION BY id%100 ORDER BY k),PERCENT_RANK() OVER(PARTITION BY id%100 ORDER BY k),CUME_DIST() OVER(PARTITION BY id%100 ORDER BY k),NTILE(7) OVER(PARTITION BY id%100 ORDER BY k) FROM t",
        "SELECT id,LAG(k,id%5-2,99) IGNORE NULLS OVER(PARTITION BY id%100 ORDER BY id DESC),LEAD(k,2,77) OVER(PARTITION BY id%100 ORDER BY id),LEAD(k,-2,101) IGNORE NULLS OVER(PARTITION BY id%100 ORDER BY id DESC) FROM t",
        "SELECT id,FIRST_VALUE(k) IGNORE NULLS OVER w,LAST_VALUE(k) OVER w,NTH_VALUE(k,3) IGNORE NULLS OVER w FROM t WINDOW w AS(PARTITION BY CASE WHEN id%100=0 THEN NULL ELSE id%100 END ORDER BY k ROWS BETWEEN 2 PRECEDING AND 3 FOLLOWING EXCLUDE TIES)",
        "SELECT id,SUM(id) OVER(PARTITION BY id%100 ORDER BY k ROWS BETWEEN 3 PRECEDING AND 2 FOLLOWING EXCLUDE CURRENT ROW),AVG(id) OVER(PARTITION BY id%100 ORDER BY k RANGE BETWEEN 2 PRECEDING AND 1 FOLLOWING EXCLUDE GROUP),COUNT(*) OVER(PARTITION BY id%100 ORDER BY k GROUPS BETWEEN 1 PRECEDING AND 2 FOLLOWING EXCLUDE TIES) FROM t",
        "SELECT id,STRING_AGG(note,',') OVER(PARTITION BY id%100 ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING),window_sum(id) OVER(PARTITION BY id%100 ORDER BY k ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING) FROM t",
        "SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY k,id),SUM(id) OVER(PARTITION BY id%100 ORDER BY k),ROW_NUMBER() OVER(PARTITION BY id%73 ORDER BY id DESC) FROM t",
        "SELECT DISTINCT ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY k) FROM t ORDER BY 1 DESC LIMIT 7 OFFSET 3",
        "SELECT stamp(id),ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY k) FROM t LIMIT 19 OFFSET 11",
        "SELECT nextval('s'),LAG(stamp(id),2,-1) OVER(PARTITION BY id%100 ORDER BY stamp(k)),ROW_NUMBER() OVER(PARTITION BY id%73 ORDER BY stamp(id) DESC) FROM t",
        "SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY nextval('s')),SUM(nextval('s')) OVER(PARTITION BY id%73 ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t",
        "SELECT t.id,LAG(t.k) OVER(PARTITION BY t.id%100 ORDER BY t.id) FROM t CROSS JOIN u",
        "SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY k),ROW_NUMBER() OVER(PARTITION BY id%100) FROM t",
        "SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY k),LAG((SELECT 3)) OVER(PARTITION BY id%100 ORDER BY id) FROM t",
        "SELECT id,COUNT(*),ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY COUNT(*) DESC) FROM t GROUP BY id",
        "SELECT id,note,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY note),LAG(CAST(note AS CHAR(5))) OVER(PARTITION BY id%100 ORDER BY id) FROM t",
    ];

    [Fact(DisplayName = "Cohesion Test [Sql] - Partitioned window spills preserve rows, input order, calls, sequences and work across sort groups (#1789)")]
    public async Task ExecuteAsync_WindowSpillCorpus_ShouldMatchFittingExecution()
    {
        // Arrange: augmented retained input is more than ten grants; each partition fits.
        var logs = new[] { new List<long>(), new List<long>() };
        await using var reference = await CreateWindowAsync("window-reference", 67108864, logs[0]);
        await using var spilled = await CreateWindowAsync("window-spilled", 262144, logs[1]);
        await using var a = await (await reference.CreateDatabaseAsync("db")).CreateSessionAsync();
        await using var b = await (await spilled.CreateDatabaseAsync("db")).CreateSessionAsync();
        await Seed(a, 12000); await Seed(b, 12000);
        foreach (string sql in _windowCorpus)
        {
            logs[0].Clear(); logs[1].Clear();
            // Act
            await using var left = (DatabaseResultSet)await a.ExecuteAsync(sql);
            await using var right = (DatabaseResultSet)await b.ExecuteAsync(sql);
            // Assert
            (await Rows(right)).ShouldBe(await Rows(left), customMessage: sql);
            logs[1].ShouldBe(logs[0], customMessage: sql);
            b.LastStatementMetrics!.RecordsExamined.ShouldBe(a.LastStatementMetrics!.RecordsExamined);
            b.LastStatementMetrics.WindowFrameWorkVisits.ShouldBe(a.LastStatementMetrics.WindowFrameWorkVisits);
            b.LastStatementMetrics.HashMemoryBytes.ShouldBe(0); b.LastStatementMetrics.PlainSelectMemoryBytes.ShouldBe(0);
            SqlHashSpillMetrics.For(b.LastStatementMetrics).ShouldNotBeNull().Runs.ShouldBeGreaterThan(0);
            _output.WriteLine($"WINDOW_CORPUS|{sql}|rows=12000|runs={SqlHashSpillMetrics.For(b.LastStatementMetrics)!.Runs}");
        }
        await using var sequenceA = (DatabaseResultSet)await a.ExecuteAsync("SELECT currval('s')");
        await using var sequenceB = (DatabaseResultSet)await b.ExecuteAsync("SELECT currval('s')");
        (await Rows(sequenceB)).ShouldBe(await Rows(sequenceA));

        await a.ExecuteAsync("CREATE TABLE archived AS SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY k) AS n FROM t");
        await b.ExecuteAsync("CREATE TABLE archived AS SELECT id,ROW_NUMBER() OVER(PARTITION BY id%100 ORDER BY k) AS n FROM t");
        await using var archivedA = (DatabaseResultSet)await a.ExecuteAsync("SELECT * FROM archived");
        await using var archivedB = (DatabaseResultSet)await b.ExecuteAsync("SELECT * FROM archived");
        (await Rows(archivedB)).ShouldBe(await Rows(archivedA));
    }

    private async Task<SqlDatabaseEngine> CreateWindowAsync(string name, long budget, List<long>? calls = null, long quota = 134217728)
    {
        var builder = SqlDatabaseEngine.CreateBuilder(name);
        builder.Options.RootPath = Path.Combine(_root, name);
        builder.Options.StatementMemoryBudget = budget; builder.Options.StatementTempSpaceQuota = quota;
        builder.Options.BufferPoolCapacity = 1048576;
        builder.Functions.Add(SqlScalarFunction.Create("stamp", (long value) => { calls?.Add(value); return value; }));
        builder.Functions.Add(SqlAggregateFunction.Create<long, int, long>("window_sum", () => 0, (s, v) => s + v, s => s));
        return await builder.BuildAsync();
    }
}
