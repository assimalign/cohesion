using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlWindowWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Windows: ranking and navigation types, values and refusals survive both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_WindowStage_ShouldPreserveValuesAndReusableDiagnostics(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE t(id INT,p INT,v INT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO t VALUES(1,1,10),(2,1,10),(3,1,20),(4,2,NULL)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var rows = await connection.QueryAsync("SELECT id,RANK() OVER w AS r,DENSE_RANK() OVER w AS d,PERCENT_RANK() OVER w AS p,CUME_DIST() OVER w AS c,NTILE(2) OVER w AS n,LAG(v,1,1.5) OVER(ORDER BY id) AS previous FROM t WINDOW w AS(PARTITION BY p ORDER BY v NULLS FIRST) ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        rows.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Int32,DatabaseType.Int64,DatabaseType.Int64,DatabaseType.Float64,DatabaseType.Float64,DatabaseType.Int32,DatabaseType.Decimal]);
        rows.Select(row => row["r"]).ShouldBe(new object?[] { 1L,1L,3L,1L });
        rows.Select(row => row["previous"]).ShouldBe(new object?[] { 1.5m,10m,10m,20m });
        var explained = await connection.QueryAsync("EXPLAIN SELECT RANK() OVER w FROM t WINDOW w AS(PARTITION BY p ORDER BY v)", cancellationToken: SqlClientTestHarness.Timeout());
        explained.Any(row => Equals(row[2], "WindowAgg") && row[5]!.ToString()!.Contains("functions=RANK", StringComparison.Ordinal)).ShouldBeTrue();
        foreach (var refusal in new[]
        {
            (Sql: "SELECT RANK() OVER missing FROM t", Code: "CS01065"),
            (Sql: "SELECT NTILE(0) OVER() FROM t", Code: "CS05078"),
            (Sql: "SELECT id FROM t WHERE RANK() OVER()>1", Code: "CS05076"),
            (Sql: "SELECT SUM(v) OVER() FROM t", Code: "CK00001"),
        })
        {
            var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(refusal.Sql, cancellationToken: SqlClientTestHarness.Timeout()));
            error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == refusal.Code);
        }
        (await connection.QueryAsync("SELECT COUNT(*) AS n FROM t", cancellationToken: SqlClientTestHarness.Timeout()))[0]["n"].ShouldBe(4L);
    }
}
