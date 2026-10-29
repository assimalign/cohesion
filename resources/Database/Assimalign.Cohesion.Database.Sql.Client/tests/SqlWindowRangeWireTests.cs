using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlWindowRangeWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Frames: RANGE offsets, GROUPS, EXCLUDE and NULL treatment execute over both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_AdvancedFrames_ShouldPreserveValuesAndDiagnosticsAsync(bool tcp)
    {
        // Arrange
        await using var harness=await SqlClientTestHarness.StartAsync(tcp:tcp);
        await using var connection=await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE TABLE t(id INT,k INT,v INT)",cancellationToken:SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("INSERT INTO t VALUES(1,1,10),(2,1,20),(3,2,NULL),(4,2,40)",cancellationToken:SqlClientTestHarness.Timeout(30));
        // Act
        var rows=await connection.QueryAsync("SELECT SUM(v) OVER(ORDER BY k RANGE 1 PRECEDING EXCLUDE GROUP) AS r,SUM(v) OVER(ORDER BY k GROUPS 1 PRECEDING EXCLUDE TIES) AS g,COUNT(*) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING EXCLUDE CURRENT ROW) AS c,LEAD(v) IGNORE NULLS OVER(ORDER BY id) AS n FROM t ORDER BY id",cancellationToken:SqlClientTestHarness.Timeout(30));
        // Assert
        rows.Select(r=>r["r"]).ShouldBe(new object?[]{null,null,30m,30m});
        rows.Select(r=>r["g"]).ShouldBe(new object?[]{10m,20m,30m,70m});
        rows.Select(r=>r["c"]).ShouldBe(new object?[]{1L,2L,2L,1L});
        rows.Select(r=>r["n"]).ShouldBe(new object?[]{20,40,40,null});
        var plan=await connection.QueryAsync("EXPLAIN ANALYZE SELECT AVG(v) OVER(ORDER BY id RANGE 1 PRECEDING EXCLUDE TIES) FROM t",cancellationToken:SqlClientTestHarness.Timeout(30));
        plan.ShouldContain(row=>Equals(row["operator"],"WindowAgg") && row["detail"]!.ToString()!.Contains("RANGE BETWEEN 1 PRECEDING AND CURRENT ROW EXCLUDE TIES",StringComparison.Ordinal) && row["detail"]!.ToString()!.Contains("inverse_transitions=",StringComparison.Ordinal));
        foreach(var (sql,code) in new[]{
            ("SELECT SUM(v) OVER(RANGE 1 PRECEDING) FROM t","CS05086"),
            ("SELECT SUM(v) OVER(GROUPS CURRENT ROW) FROM t","CS05087"),
            ("SELECT SUM(v) OVER(ORDER BY CAST(id AS TEXT) RANGE 1 PRECEDING) FROM t","CS02074"),
            ("SELECT SUM(v) OVER(ORDER BY id RANGE 1.5 PRECEDING) FROM t","CS02075"),
            ("SELECT SUM(v) OVER(ORDER BY id RANGE -1 PRECEDING) FROM t","CS02076"),
            ("SELECT RANK() IGNORE NULLS OVER() FROM t","CS05088"),
        })
        {
            var failure=await Should.ThrowAsync<SqlClientException>(async()=>await connection.QueryAsync(sql,cancellationToken:SqlClientTestHarness.Timeout(30)));
            failure.Diagnostics.ShouldContain(d=>d.Code==code); failure.ConnectionUsable.ShouldBeTrue();
        }
        (await connection.QueryAsync("SELECT COUNT(*) AS n FROM t",cancellationToken:SqlClientTestHarness.Timeout(30))).Single()["n"].ShouldBe(4L);
    }
}
