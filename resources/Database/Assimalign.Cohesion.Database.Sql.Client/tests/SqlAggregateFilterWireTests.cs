using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlAggregateFilterWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - FILTER and statistics: values and metadata survive both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_FilteredAggregates_ShouldRetainValuesAndTypes(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE samples(v INT, f DOUBLE PRECISION, b BOOLEAN, label TEXT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO samples VALUES(0,0,FALSE,'z'),(1,1,TRUE,'b'),(3,3,TRUE,'a'),(NULL,NULL,NULL,NULL)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var result = await connection.QueryAsync(
            "SELECT COUNT(*) FILTER (WHERE b) AS n, SUM(DISTINCT 3/v) FILTER (WHERE b) AS total, " +
            "STRING_AGG(label, ',' ORDER BY v DESC) FILTER (WHERE b) AS labels, " +
            "LISTAGG(label, ',') WITHIN GROUP (ORDER BY v DESC) FILTER (WHERE b) AS listed, " +
            "BOOL_AND(b) FILTER (WHERE b) AS all_true, EVERY(b) AS every_true, BOOL_OR(b) AS any_true, " +
            "VAR_SAMP(v) FILTER (WHERE b) AS variance, STDDEV_POP(v) FILTER (WHERE b) AS deviation, " +
            "VAR_POP(f) FILTER (WHERE b) AS floating, STDDEV(v) FILTER (WHERE FALSE) AS empty, " +
            "VAR_POP(CASE WHEN v=1 THEN 1 ELSE CAST(3 AS DOUBLE PRECISION) END) FILTER (WHERE b) AS mixed " +
            "FROM samples HAVING COUNT(*) FILTER (WHERE b)=2 ORDER BY VAR_POP(v) FILTER (WHERE b)",
            cancellationToken: SqlClientTestHarness.Timeout());
        var explained = await connection.QueryAsync("EXPLAIN SELECT STDDEV(v) FILTER (WHERE b) FROM samples", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe(new[] { DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.String, DatabaseType.String,
            DatabaseType.Boolean, DatabaseType.Boolean, DatabaseType.Boolean, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Float64, DatabaseType.Decimal, DatabaseType.Float64 });
        var row = result.ShouldHaveSingleItem();
        row["n"].ShouldBe(2L); row["total"].ShouldBe(4m); row["labels"].ShouldBe("a,b"); row["listed"].ShouldBe("a,b");
        row["all_true"].ShouldBe(true); row["every_true"].ShouldBe(false); row["any_true"].ShouldBe(true);
        row["variance"].ShouldBe(2m); row["deviation"].ShouldBe(1m); row["floating"].ShouldBe(1d); row["empty"].ShouldBeNull();
        row["mixed"].ShouldBe(1d);
        explained.Any(plan => plan[5] is string detail && detail.Contains("filtered_aggregates=1", System.StringComparison.Ordinal)).ShouldBeTrue();
    }
}
