using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlReturningWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - RETURNING: parameters survive the in-memory and TCP exchanges")]
    [InlineData(false, "INSERT INTO t VALUES (1) RETURNING @p, id + @p")]
    [InlineData(true, "INSERT INTO t VALUES (1) RETURNING @p, id + @p")]
    [InlineData(false, "INSERT INTO t SELECT id FROM t RETURNING @p, id + @p")]
    [InlineData(true, "INSERT INTO t SELECT id FROM t RETURNING @p, id + @p")]
    [InlineData(false, "UPDATE t SET id = 1 RETURNING @p, id + @p")]
    [InlineData(true, "UPDATE t SET id = 1 RETURNING @p, id + @p")]
    [InlineData(false, "DELETE FROM t RETURNING @p, id + @p")]
    [InlineData(true, "DELETE FROM t RETURNING @p, id + @p")]
    public async Task QueryAsync_ParameterizedReturning_ShouldBindParameters(bool tcp, string sql)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: SqlClientTestHarness.Timeout());
        if (!sql.StartsWith("INSERT INTO t VALUES", System.StringComparison.Ordinal))
        {
            await connection.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: SqlClientTestHarness.Timeout());
        }

        // Act
        var result = await connection.QueryAsync(sql, new Dictionary<string, object?> { ["p"] = 7 }, SqlClientTestHarness.Timeout());

        // Assert
        result.AffectedCount.ShouldBe(1);
        var row = result.ShouldHaveSingleItem();
        row[0].ShouldBe(7);
        row[1].ShouldBe(8L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - RETURNING: rows, affected count and all client verbs share one exchange")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Returning_ShouldExposeRowsAndCount(bool tcp)
    {
        // Arrange
        var observer = new RecordingObserver();
        await using var harness = await SqlClientTestHarness.StartAsync(observer: observer, tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE t (id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name TEXT)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var inserted = await connection.QueryAsync("INSERT INTO t (name) VALUES ('x'), ('y') RETURNING id, name", cancellationToken: SqlClientTestHarness.Timeout());
        var updated = await connection.QueryAsync("UPDATE t SET name = UPPER(name) RETURNING id, name", cancellationToken: SqlClientTestHarness.Timeout());
        var deleted = await connection.QueryAsync("DELETE FROM t WHERE id = 2 RETURNING *", cancellationToken: SqlClientTestHarness.Timeout());
        var empty = await connection.QueryAsync("DELETE FROM t WHERE id = 99 RETURNING *", cancellationToken: SqlClientTestHarness.Timeout());
        long count = await connection.ExecuteAsync("UPDATE t SET name = 'count' RETURNING *", cancellationToken: SqlClientTestHarness.Timeout());
        int identity = await connection.ExecuteScalarAsync<int>(new SqlCommand("INSERT INTO t (name) VALUES ('scalar') RETURNING id"), cancellationToken: SqlClientTestHarness.Timeout());
        var selected = await connection.QueryAsync("SELECT id FROM t", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        inserted.Columns.Select(column => (column.Name, column.Type)).ShouldBe([("id", DatabaseType.Int32), ("name", DatabaseType.String)]);
        inserted.Select(row => (row.GetInt32(0), row.GetString(1))).ShouldBe([(1, "x"), (2, "y")]);
        inserted.AffectedCount.ShouldBe(2);
        updated.OrderBy(row => row.GetInt32(0)).Select(row => (row.GetInt32(0), row.GetString(1))).ShouldBe([(1, "X"), (2, "Y")]);
        updated.AffectedCount.ShouldBe(2);
        deleted.ShouldHaveSingleItem()["name"].ShouldBe("Y");
        deleted.AffectedCount.ShouldBe(1);
        empty.ShouldBeEmpty();
        empty.Columns.Count.ShouldBe(2);
        empty.AffectedCount.ShouldBe(0);
        selected.AffectedCount.ShouldBe(-1);
        count.ShouldBe(1);
        identity.ShouldBe(3);
        observer.Executed.Where(item => item.CommandText.Contains("RETURNING", System.StringComparison.Ordinal))
            .Select(item => (item.RowCount, item.AffectedCount)).ShouldBe([(2L, 2L), (2L, 2L), (1L, 1L), (0L, 0L), (1L, 1L), (1L, 1L)]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - RETURNING: excluded forms reject before mutation and keep the connection usable")]
    [InlineData("INSERT INTO t OUTPUT inserted.* VALUES (3, 4)")]
    [InlineData("DELETE FROM t OUTPUT deleted.* WHERE id = 1")]
    [InlineData("DELETE FROM t RETURNING id INTO v")]
    [InlineData("DELETE FROM t RETURNING OLD.age")]
    [InlineData("UPDATE t SET age = 0 RETURNING NEW.age")]
    [InlineData("DELETE FROM t RETURNING WITH (OLD AS o) *")]
    [InlineData("UPDATE t SET age = 1 RETURNING (SELECT MAX(id) FROM t)")]
    [InlineData("INSERT INTO t VALUES (3, 4) RETURNING (SELECT 1)")]
    [InlineData("INSERT INTO t SELECT * FROM t RETURNING (SELECT 1)")]
    [InlineData("DELETE FROM t WHERE id = 1 RETURNING (SELECT 1)")]
    [InlineData("UPDATE t SET age = 0 RETURNING ROW_NUMBER() OVER ()")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d")]
    public async Task QueryAsync_ExcludedReturning_ShouldRejectWithoutWrites(string sql)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE t (id INT, age INT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO t VALUES (1, 5)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        failure.Message.ShouldContain("CK00001", Case.Sensitive);
        failure.ConnectionUsable.ShouldBeTrue();
        var result = await connection.QueryAsync("SELECT id, age FROM t", cancellationToken: SqlClientTestHarness.Timeout());
        result.ShouldHaveSingleItem()["age"].ShouldBe(5);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - RETURNING: evaluation and planning failures retain coded usable exchanges")]
    [InlineData("INSERT INTO t VALUES (3, 1), (4, 0) RETURNING 1 / age", "CS02001")]
    [InlineData("UPDATE t SET age = 1 RETURNING COUNT(*)", "CS05017")]
    public async Task QueryAsync_ReturningFailure_ShouldLeaveConnectionUsable(string sql, string code)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE t (id INT, age INT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO t VALUES (1, 5)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("BEGIN", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Message.ShouldContain(code, Case.Sensitive);
        failure.ConnectionUsable.ShouldBeTrue();
        await connection.ExecuteAsync("COMMIT", cancellationToken: SqlClientTestHarness.Timeout());
        (await connection.QueryAsync("SELECT age FROM t", cancellationToken: SqlClientTestHarness.Timeout())).ShouldHaveSingleItem()[0].ShouldBe(5);
    }
}
