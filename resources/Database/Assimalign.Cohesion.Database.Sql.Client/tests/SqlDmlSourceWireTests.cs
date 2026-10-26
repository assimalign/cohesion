using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlDmlSourceWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Joined DML: source values, visible star metadata and deduplicated deletes cross both transports (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_SourceReturning_ShouldPreserveValuesMetadataAndCounts(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        foreach (string sql in new[] { "CREATE TABLE t (id INT PRIMARY KEY,v INT)", "CREATE TABLE s (id INT,label VARCHAR(23) NOT NULL)",
            "CREATE TABLE u (id INT,amount DECIMAL(9,2) NOT NULL)", "INSERT INTO t VALUES(1,0),(2,0)",
            "INSERT INTO s VALUES(1,'a'),(2,'b')", "INSERT INTO u VALUES(1,8.25)" })
        { await connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()); }

        // Act
        var updated = await connection.QueryAsync("WITH q AS (SELECT id,label FROM s) UPDATE t SET v=COALESCE(u.id,9) FROM q LEFT JOIN u USING(id) WHERE t.id=q.id RETURNING *",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO s VALUES(1,'a')", cancellationToken: SqlClientTestHarness.Timeout());
        var deleted = await connection.QueryAsync("DELETE FROM t USING s WHERE t.id=s.id RETURNING t.id,t.v,s.label", cancellationToken: SqlClientTestHarness.Timeout());
        var empty = await connection.QueryAsync("UPDATE t SET v=s.id FROM s RETURNING t.id,s.label", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        updated.AffectedCount.ShouldBe(2);
        updated.Columns.Select(column => column.Name).ShouldBe(["id","v","id","label","amount"]);
        updated.Columns[3].MaxLength.ShouldBe(23);
        updated.OrderBy(row => row.GetInt32(0)).Select(row => (row.GetInt32(0),row.GetInt32(1),row.GetString(3),row.GetValue(4)))
            .ShouldBe([(1,1,"a",(object?)8.25m),(2,9,"b",null)]);
        deleted.AffectedCount.ShouldBe(2);
        deleted.OrderBy(row => row.GetInt32(0)).Select(row => (row.GetInt32(0),row.GetInt32(1),row.GetString(2))).ShouldBe([(1,1,"a"),(2,9,"b")]);
        empty.AffectedCount.ShouldBe(0);
        empty.ShouldBeEmpty();
        empty.Columns.Select(column => column.Name).ShouldBe(["id","label"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - UPDATE FROM: multiple matches fail atomically with CS05068 and preserve explicit work on both transports (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_MultipleSources_ShouldKeepConnectionAndTransactionUsable(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        foreach (string sql in new[] { "CREATE TABLE t (id INT PRIMARY KEY,v INT)", "CREATE TABLE s (id INT,v INT)",
            "INSERT INTO t VALUES(1,0),(2,0)", "INSERT INTO s VALUES(1,10),(2,20),(2,20)", "BEGIN", "INSERT INTO t VALUES(3,5)" })
        { await connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()); }

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "UPDATE t SET v=s.v FROM s WHERE t.id=s.id RETURNING s.v", cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05068");
        failure.Diagnostics[0].Start.ShouldNotBeNull();
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT v FROM t ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout())).Select(row => row.GetInt32(0)).ShouldBe([0,0,5]);
        await connection.ExecuteAsync("COMMIT", cancellationToken: SqlClientTestHarness.Timeout());
        (await connection.ExecuteAsync("DELETE FROM t USING s WHERE t.id=s.id", cancellationToken: SqlClientTestHarness.Timeout())).ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Joined DML: constraints roll back every effect and leave both transports reusable (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_ConstraintFailure_ShouldRetainRows(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        foreach (string sql in new[] { "CREATE TABLE t (id INT PRIMARY KEY,v INT CHECK(v>0))", "CREATE TABLE s (id INT,v INT)",
            "INSERT INTO t VALUES(1,10),(2,20)", "INSERT INTO s VALUES(1,11),(2,-1)" })
        { await connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()); }

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "UPDATE t SET v=s.v FROM s WHERE t.id=s.id", cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03038");
        failure.ConnectionUsable.ShouldBeTrue();
        (await connection.QueryAsync("SELECT v FROM t ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout())).Select(row => row.GetInt32(0)).ShouldBe([10,20]);
    }
}
