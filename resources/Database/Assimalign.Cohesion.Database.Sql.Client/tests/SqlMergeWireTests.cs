using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlMergeWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - MERGE: actions, BY SOURCE, RETURNING and repeated-target failure round trip (#1715)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_Merge_ShouldReturnActionsAndPreserveConnectionAsync(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        await connection.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,v INT)", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("CREATE TABLE s(id INT,v INT)", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("INSERT INTO t VALUES(1,10),(2,20)", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("INSERT INTO s VALUES(1,11),(3,33)", cancellationToken: timeout.Token);

        // Act
        var rows = await connection.QueryAsync("MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=s.v " +
            "WHEN NOT MATCHED THEN INSERT VALUES(s.id,s.v) WHEN NOT MATCHED BY SOURCE THEN DELETE RETURNING t.id,t.v,s.id,merge_action()", cancellationToken: timeout.Token);

        // Assert
        rows.OrderBy(row => row[0]).Select(row => string.Join(",", Enumerable.Range(0, row.FieldCount).Select(row.GetValue))).ShouldBe(["1,11,1,UPDATE", "2,20,,DELETE", "3,33,3,INSERT"]);
        await connection.ExecuteAsync("INSERT INTO s VALUES(1,12)", cancellationToken: timeout.Token);
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN DELETE", cancellationToken: timeout.Token));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05036");
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT id,v FROM t ORDER BY id", cancellationToken: timeout.Token)).Select(row => string.Join(",", Enumerable.Range(0, row.FieldCount).Select(row.GetValue))).ShouldBe(["1,11", "3,33"]);
    }
}
