using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlJoinOrderSearchWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Join search: reordered results and EXPLAIN survive InMemory and TCP (#1629)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ExactJoinSearch_ShouldExposeChosenOrder(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout(30));
        foreach (string table in new[] { "a", "b", "c" })
        {
            await connection.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT)", cancellationToken: SqlClientTestHarness.Timeout(30));
            int count = table == "c" ? 1 : 24;
            await connection.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1, count).Select(id => $"({id},{id})")), cancellationToken: SqlClientTestHarness.Timeout(30));
        }
        const string sql = "SELECT a.id,b.id,c.id FROM a CROSS JOIN b JOIN c ON a.k=c.k AND b.k=c.k ORDER BY a.id";

        // Act
        var rows = await connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout(30));
        var explain = await connection.QueryAsync("EXPLAIN " + sql, cancellationToken: SqlClientTestHarness.Timeout(30));

        // Assert
        var row = rows.ShouldHaveSingleItem();
        row[0].ShouldBe(1); row[1].ShouldBe(1); row[2].ShouldBe(1);
        explain.Select(plan => Convert.ToString(plan[5])).ShouldContain(detail => detail != null &&
            detail.Contains("join_search=exact_dp", StringComparison.Ordinal) && detail.Contains("reordered=true", StringComparison.Ordinal));
    }
}
