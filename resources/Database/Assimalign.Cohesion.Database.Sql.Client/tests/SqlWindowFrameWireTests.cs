using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlWindowFrameWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Frames: ROWS, default peers, value functions, FILTER and refusals survive both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_FramedWindows_ShouldPreserveValuesAndCodesAsync(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE TABLE t(id INT,k INT,v INT,b BOOLEAN)", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("INSERT INTO t VALUES(1,1,10,TRUE),(2,1,20,FALSE),(3,2,NULL,NULL),(4,2,40,TRUE)", cancellationToken: SqlClientTestHarness.Timeout(30));
        // Act
        var rows = await connection.QueryAsync("SELECT id,SUM(v) OVER(ORDER BY k) AS peers,SUM(v) FILTER(WHERE b) OVER w AS filtered,FIRST_VALUE(v) OVER w AS first,LAST_VALUE(v) OVER w AS last,NTH_VALUE(v,2) OVER w AS nth,BOOL_OR(b) OVER w AS any_true,VAR_POP(v) OVER w AS variance FROM t WINDOW w AS(ORDER BY id ROWS 1 PRECEDING) ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout(30));
        // Assert
        rows.Columns.Select(c => c.Type).ShouldBe([DatabaseType.Int32,DatabaseType.Decimal,DatabaseType.Decimal,DatabaseType.Int32,DatabaseType.Int32,DatabaseType.Int32,DatabaseType.Boolean,DatabaseType.Decimal]);
        rows.Select(r => r["peers"]).ShouldBe(new object?[] { 30m,30m,70m,70m });
        rows.Select(r => r["filtered"]).ShouldBe(new object?[] { 10m,10m,null,40m });
        rows.Select(r => r["first"]).ShouldBe(new object?[] { 10,10,20,null });
        rows.Select(r => r["last"]).ShouldBe(new object?[] { 10,20,null,40 });
        rows.Select(r => r["nth"]).ShouldBe(new object?[] { null,20,null,40 });
        rows.Select(r => r["variance"]).ShouldBe(new object?[] { 0m,25m,0m,0m });
        var explained = await connection.QueryAsync("EXPLAIN ANALYZE SELECT SUM(v) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM t", cancellationToken: SqlClientTestHarness.Timeout(30));
        explained.ShouldContain(row => row["operator"]!.ToString() == "WindowAgg" && row["detail"]!.ToString()!.Contains("frame_visits=4", StringComparison.Ordinal));
        foreach (var (sql,code) in new[]
        {
            ("SELECT SUM(v) OVER(ROWS NULL PRECEDING) FROM t", "CS02071"),
            ("SELECT SUM(v) OVER(ROWS -1 PRECEDING) FROM t", "CS02072"),
            ("SELECT SUM(v) OVER(ROWS 1.5 PRECEDING) FROM t", "CS02070"),
            ("SELECT SUM(v) OVER(ROWS 1 FOLLOWING) FROM t", "CS05080"),
            ("SELECT NTH_VALUE(v,0) OVER() FROM t", "CS05082"),
            ("SELECT SUM(v) OVER(w) FROM t WINDOW w AS(ROWS CURRENT ROW)", "CS01066"),
            ("SELECT SUM(DISTINCT v) OVER() FROM t", "CK00001"),
        })
        {
            var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout(30)));
            failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
            failure.Diagnostics.ShouldContain(d => d.Code == code);
            failure.ConnectionUsable.ShouldBeTrue();
        }
        (await connection.QueryAsync("SELECT COUNT(*) AS n FROM t", cancellationToken: SqlClientTestHarness.Timeout(30))).ShouldHaveSingleItem()["n"].ShouldBe(4L);
    }
}
