using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlDirectedIndexWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Directed keys: seeks, ranges, metadata and syntax refusals cross InMemory and TCP (#1631)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DirectedIndex_ShouldReturnOrderedRangesAndMetadata(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE TABLE t(id INT,a INT,b INT)", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("INSERT INTO t VALUES(1,1,2),(2,1,3),(3,1,NULL),(4,2,3),(5,1,1)", cancellationToken: SqlClientTestHarness.Timeout(30));
        await connection.ExecuteAsync("CREATE INDEX ix ON t(a DESC NULLS LAST,b ASC NULLS FIRST)", cancellationToken: SqlClientTestHarness.Timeout(30));

        // Act
        var rows = await connection.QueryAsync("SELECT id FROM t WHERE a=1 AND b>=2 AND b<=3 ORDER BY b DESC", cancellationToken: SqlClientTestHarness.Timeout(30));
        var metadata = await connection.QueryAsync("SELECT DIRECTION,NULL_PLACEMENT FROM INFORMATION_SCHEMA.INDEXES WHERE INDEX_NAME='ix' ORDER BY ORDINAL_POSITION", cancellationToken: SqlClientTestHarness.Timeout(30));
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "CREATE TABLE bad(a INT,PRIMARY KEY(a DESC))", cancellationToken: SqlClientTestHarness.Timeout(30)));

        // Assert
        rows.Select(row => row[0]).ShouldBe([2, 1]);
        metadata.Select(row => new object?[] { row[0], row[1] }).ShouldBe(new object?[][] { ["DESC", "LAST"], [null, "FIRST"] });
        failure.Diagnostics.ShouldContain(d => d.Code == "CS00003");
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT id FROM t WHERE a=1 ORDER BY b ASC NULLS FIRST", cancellationToken: SqlClientTestHarness.Timeout(30)))
            .Select(row => row[0]).ShouldBe([3, 5, 1, 2]);
        var plan = await connection.QueryAsync("EXPLAIN SELECT id FROM t WHERE a=1 ORDER BY b ASC NULLS FIRST", cancellationToken: SqlClientTestHarness.Timeout(30));
        plan.ShouldContain(row => (row[5] as string ?? "").Contains("order_keys=1", StringComparison.Ordinal));
    }
}
