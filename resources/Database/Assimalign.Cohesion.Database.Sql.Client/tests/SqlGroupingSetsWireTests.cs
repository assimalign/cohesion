using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlGroupingSetsWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Grouping sets: formerly refused forms execute with independent totals on both transports")]
    [InlineData(false, "ROLLUP(name)")]
    [InlineData(true, "ROLLUP(name)")]
    [InlineData(false, "CUBE(name)")]
    [InlineData(true, "CUBE(name)")]
    [InlineData(false, "GROUPING SETS ((name), ())")]
    [InlineData(true, "GROUPING SETS ((name), ())")]
    public async Task QueryAsync_FormerlyExcludedForms_ShouldReturnDetailsAndTotals(bool tcp, string grouping)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var result = await connection.QueryAsync($"SELECT name,GROUPING(name) AS mask,COUNT(*) AS n FROM users GROUP BY {grouping} ORDER BY mask,name", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Count.ShouldBe(3);
        result.Select(row => row["name"]).ShouldBe(new object?[] { "ada", "grace", null });
        result.Select(row => row["mask"]).ShouldBe(new object?[] { 0, 0, 1 });
        result.Select(row => row["n"]).ShouldBe(new object?[] { 1L, 1L, 2L });
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Grouping sets: subtotals, FILTER, statistics and masks survive both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_SubtotalMasks_ShouldPreserveValuesAndTypes(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE t(a INT,b INT,v INT,flag BOOLEAN,label TEXT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO t VALUES(NULL,1,0,FALSE,'z'),(1,1,2,TRUE,'b'),(1,2,4,TRUE,'a')", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var result = await connection.QueryAsync("SELECT a,b,GROUPING(a,b) AS mask,GROUPING_ID(a,b) AS alias_mask," +
            "COUNT(*) AS n,SUM(DISTINCT 4/v) FILTER(WHERE flag) AS total,BOOL_AND(flag) AS every_flag," +
            "VARIANCE(v) FILTER(WHERE flag) AS variance,STRING_AGG(label,',' ORDER BY v) FILTER(WHERE flag) AS labels " +
            "FROM t GROUP BY ROLLUP(a,b) HAVING GROUPING(a,b)>0 ORDER BY mask,a NULLS LAST", cancellationToken: SqlClientTestHarness.Timeout());
        var explained = await connection.QueryAsync("EXPLAIN SELECT COUNT(*) FROM t GROUP BY CUBE(a,b)", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe(new[] { DatabaseType.Int32, DatabaseType.Int32, DatabaseType.Int32,
            DatabaseType.Int32, DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.Boolean, DatabaseType.Decimal, DatabaseType.String });
        result.Count.ShouldBe(3);
        result[0]["a"].ShouldBe(1); result[0]["b"].ShouldBeNull(); result[0]["mask"].ShouldBe(1);
        result[0]["total"].ShouldBe(3m); result[0]["variance"].ShouldBe(2m); result[0]["labels"].ShouldBe("b,a");
        result[1]["a"].ShouldBeNull(); result[1]["mask"].ShouldBe(1); result[1]["n"].ShouldBe(1L);
        result[2]["a"].ShouldBeNull(); result[2]["mask"].ShouldBe(3); result[2]["alias_mask"].ShouldBe(3);
        result[2]["n"].ShouldBe(3L); result[2]["every_flag"].ShouldBe(false);
        explained.Any(row => row[5] is string detail && detail.Contains("grouping_sets=4", StringComparison.Ordinal)).ShouldBeTrue();

        // Act / Assert: a coded planning failure leaves the same connection usable.
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(
            "SELECT GROUPING(v) FROM t GROUP BY CUBE(a,b)", cancellationToken: SqlClientTestHarness.Timeout()));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS01062");
        (await connection.QueryAsync("SELECT COUNT(*) AS n FROM t GROUP BY ()", cancellationToken: SqlClientTestHarness.Timeout()))[0]["n"].ShouldBe(3L);
        var nulls = await connection.QueryAsync("SELECT a,GROUPING(a) AS mask FROM t GROUP BY CUBE(a) ORDER BY mask,a NULLS LAST", cancellationToken: SqlClientTestHarness.Timeout());
        nulls.Count.ShouldBe(3);
        nulls[1]["a"].ShouldBeNull(); nulls[1]["mask"].ShouldBe(0);
        nulls[2]["a"].ShouldBeNull(); nulls[2]["mask"].ShouldBe(1);
        var empty = await connection.QueryAsync("SELECT GROUPING(a) AS mask,COUNT(*) AS n,BOOL_AND(flag) AS b,STDDEV(v) AS s FROM t WHERE FALSE GROUP BY CUBE(a)", cancellationToken: SqlClientTestHarness.Timeout());
        empty.ShouldHaveSingleItem()["mask"].ShouldBe(1);
        empty[0]["n"].ShouldBe(0L); empty[0]["b"].ShouldBeNull(); empty[0]["s"].ShouldBeNull();
        foreach (var refusal in new[]
        {
            (Sql: "SELECT GROUPING(a) FROM t", Code: "CS05074"),
            (Sql: "SELECT GROUPING() FROM t GROUP BY a", Code: "CS01063"),
            (Sql: "SELECT COUNT(*) FROM t GROUP BY DISTINCT CUBE(" + string.Join(',', Enumerable.Repeat("a",13)) + ")", Code: "CS05075"),
        })
        {
            var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(refusal.Sql, cancellationToken: SqlClientTestHarness.Timeout()));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(refusal.Code);
        }
        (await connection.QueryAsync("SELECT 1 AS n", cancellationToken: SqlClientTestHarness.Timeout())).ShouldHaveSingleItem()["n"].ShouldBe(1L);
    }
}
