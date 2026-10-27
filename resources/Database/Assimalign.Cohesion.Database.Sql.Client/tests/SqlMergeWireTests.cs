using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlMergeWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - MERGE: final FK state, overlapping writes, constraints and atomic RETURNING conform on both transports (#1715)")]
    [InlineData(false, "CASCADE", "same", null)]
    [InlineData(true, "CASCADE", "same", null)]
    [InlineData(false, "SET NULL", "same", null)]
    [InlineData(true, "SET NULL", "same", null)]
    [InlineData(false, "SET DEFAULT", "same", null)]
    [InlineData(true, "SET DEFAULT", "same", null)]
    [InlineData(false, "NO ACTION", "moved", null)]
    [InlineData(true, "NO ACTION", "moved", null)]
    [InlineData(false, "NO ACTION", "same", "CS03039")]
    [InlineData(true, "NO ACTION", "same", "CS03039")]
    [InlineData(false, "CASCADE", "check", "CS03038")]
    [InlineData(true, "CASCADE", "check", "CS03038")]
    [InlineData(false, "CASCADE", "unique", "CS03037")]
    [InlineData(true, "CASCADE", "unique", "CS03037")]
    [InlineData(false, "SET NULL", "returning", "CS02001")]
    [InlineData(true, "SET NULL", "returning", "CS02001")]
    public async Task ExecuteAsync_FinalStateCorpus_ShouldConformAndReuseTransaction(bool tcp, string policy, string scenario, string? code)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        await connection.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,pid INT DEFAULT 9 REFERENCES t(id) ON DELETE " + policy + ",v INT CHECK(v>0))", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("INSERT INTO t VALUES(1,NULL,10),(2,1,20),(9,NULL,90)", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
        await connection.ExecuteAsync("INSERT INTO t VALUES(8,NULL,80)", cancellationToken: timeout.Token);
        string source = scenario == "unique" ? "SELECT 1 AS id UNION ALL SELECT 3 UNION ALL SELECT 4" : "SELECT 1 AS id UNION ALL SELECT 2";
        string action = scenario == "unique" ? "WHEN NOT MATCHED THEN INSERT VALUES(2,1,30)"
            : "WHEN MATCHED THEN UPDATE SET pid=" + (scenario == "moved" ? "9" : "t.pid") + ",v=" + (scenario == "check" ? "-1" : "t.v+1");
        string sql = "MERGE INTO t USING (" + source + ") AS s ON t.id=s.id WHEN MATCHED AND t.id=1 THEN DELETE " + action +
            " RETURNING t.id,t.pid,t.v,s.id,merge_action()" + (scenario == "returning" ? ",1/(t.id-2)" : "");

        // Act / Assert
        if (code is null)
        {
            var rows = await connection.QueryAsync(sql, cancellationToken: timeout.Token);
            rows.AffectedCount.ShouldBe(2);
            rows.OrderBy(row => row[0]).Select(Row).ShouldBe(["1,,10,1,DELETE", "2," + (scenario == "moved" ? "9" : "1") + ",21,2,UPDATE"]);
            string? child = policy switch { "CASCADE" => null, "SET NULL" => "2,,21", _ => "2,9,21" };
            var expected = child is null ? new[] { "8,,80", "9,,90" } : [child, "8,,80", "9,,90"];
            (await connection.QueryAsync("SELECT id,pid,v FROM t ORDER BY id", cancellationToken: timeout.Token)).Select(Row).ShouldBe(expected);
        }
        else
        {
            var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(sql, cancellationToken: timeout.Token));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            failure.ConnectionUsable.ShouldBeTrue();
            (await connection.QueryAsync("SELECT id,pid,v FROM t ORDER BY id", cancellationToken: timeout.Token)).Select(Row)
                .ShouldBe(["1,,10", "2,1,20", "8,,80", "9,,90"]);
        }
        var noOp = await connection.QueryAsync("MERGE INTO t USING (SELECT 99 AS id) AS s ON t.id=s.id " +
            "WHEN MATCHED THEN DO NOTHING WHEN NOT MATCHED THEN DO NOTHING RETURNING merge_action()", cancellationToken: timeout.Token);
        noOp.Count.ShouldBe(0);
        noOp.AffectedCount.ShouldBe(0);
        await connection.ExecuteAsync("COMMIT", cancellationToken: timeout.Token);
        (await connection.QueryAsync("SELECT v FROM t WHERE id=8", cancellationToken: timeout.Token)).ShouldHaveSingleItem()[0].ShouldBe(80);
    }

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
        rows.AffectedCount.ShouldBe(3);
        rows.OrderBy(row => row[0]).Select(row => string.Join(",", Enumerable.Range(0, row.FieldCount).Select(row.GetValue))).ShouldBe(["1,11,1,UPDATE", "2,20,,DELETE", "3,33,3,INSERT"]);
        await connection.ExecuteAsync("INSERT INTO s VALUES(1,12)", cancellationToken: timeout.Token);
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN DELETE", cancellationToken: timeout.Token));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05036");
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT id,v FROM t ORDER BY id", cancellationToken: timeout.Token)).Select(row => string.Join(",", Enumerable.Range(0, row.FieldCount).Select(row.GetValue))).ShouldBe(["1,11", "3,33"]);
    }

    private static string Row(SqlRow row) => string.Join(',', Enumerable.Range(0, row.FieldCount).Select(row.GetValue));
}
