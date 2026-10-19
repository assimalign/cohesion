using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

public sealed class SqlUpsertWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Upsert: results carry inserted plus updated counts over each transport")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_UpsertReturning_ShouldCarryRowsAndCount(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, age INT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO t VALUES (1, 10)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var updated = await connection.QueryAsync("INSERT INTO t VALUES (1, 20), (2, 30) ON CONFLICT (id) DO UPDATE SET age = EXCLUDED.age RETURNING id, age", cancellationToken: SqlClientTestHarness.Timeout());
        var skipped = await connection.QueryAsync("INSERT INTO t VALUES (1, 40) ON CONFLICT DO NOTHING RETURNING *", cancellationToken: SqlClientTestHarness.Timeout());
        long affected = await connection.ExecuteAsync("INSERT INTO t VALUES (1, 50), (3, 60) ON CONFLICT (id) DO UPDATE SET age = EXCLUDED.age", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        updated.AffectedCount.ShouldBe(2);
        updated.Select(row => (row.GetInt32(0), row.GetInt32(1))).ShouldBe([(1, 20), (2, 30)]);
        skipped.AffectedCount.ShouldBe(0);
        skipped.ShouldBeEmpty();
        affected.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Client] - Upsert: the completeness regression inserts into users without a unique index")]
    public async Task ExecuteAsync_UntargetedWithoutIndex_ShouldInsert()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        long count = await connection.ExecuteAsync("INSERT INTO users (id, name, score) VALUES (3, 'linus', 300) ON CONFLICT DO NOTHING", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        count.ShouldBe(1);
        var selected = await connection.QueryAsync("SELECT name FROM users WHERE id = 3", cancellationToken: SqlClientTestHarness.Timeout());
        selected.ShouldHaveSingleItem()[0].ShouldBe("linus");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Upsert: arbiter, cardinality and constraint failures preserve usable connections")]
    [InlineData("INSERT INTO t VALUES (3, 30) ON CONFLICT (age) DO NOTHING", "COHSQLE035")]
    [InlineData("INSERT INTO t VALUES (1, 30), (1, 40) ON CONFLICT (id) DO UPDATE SET age = EXCLUDED.age", "COHSQLE036")]
    [InlineData("INSERT INTO t VALUES (3, 30), (1, 40) ON CONFLICT (id) DO UPDATE SET age = 20", "UNIQUE")]
    [InlineData("INSERT INTO t VALUES (1, 30) ON CONFLICT (id) DO UPDATE SET age = -1", "CHECK")]
    public async Task ExecuteAsync_UpsertFailure_ShouldKeepConnectionUsable(string sql, string message)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, age INT CHECK(age > 0))", cancellationToken: SqlClientTestHarness.Timeout());
        // Only an index on age in the constraint-failure case; otherwise (age) must have no arbiter.
        if (message == "UNIQUE") { await connection.ExecuteAsync("CREATE UNIQUE INDEX uq ON t(age)", cancellationToken: SqlClientTestHarness.Timeout()); }
        await connection.ExecuteAsync("INSERT INTO t VALUES (1, 10), (2, 20)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.Message.ShouldContain(message, Case.Sensitive);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(message switch
        {
            "UNIQUE" => "COHSQLE037",
            "CHECK" => "COHSQLE038",
            _ => message,
        });
        failure.ConnectionUsable.ShouldBeTrue();
        var rows = await connection.QueryAsync("SELECT id, age FROM t ORDER BY id", cancellationToken: SqlClientTestHarness.Timeout());
        rows.Select(row => (row.GetInt32(0), row.GetInt32(1))).ShouldBe([(1, 10), (2, 20)]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - Upsert: excluded spellings carry COHDBL001 ParseFailure without writes")]
    [InlineData("INSERT INTO t VALUES (2, 20) ON DUPLICATE KEY UPDATE age = 3", "ON DUPLICATE KEY UPDATE")]
    [InlineData("INSERT OR IGNORE INTO t VALUES (2, 20)", "ON CONFLICT")]
    [InlineData("INSERT OR REPLACE INTO t VALUES (2, 20)", "ON CONFLICT")]
    [InlineData("INSERT INTO t VALUES (2, 20) ON CONFLICT (lower(age)) DO NOTHING", "ON CONFLICT inference")]
    [InlineData("INSERT INTO t VALUES (2, 20) ON CONFLICT (id) WHERE age > 0 DO NOTHING", "ON CONFLICT target WHERE")]
    [InlineData("INSERT INTO t VALUES (2, 20) ON CONFLICT (age COLLATE x) DO NOTHING", "ON CONFLICT inference")]
    [InlineData("INSERT INTO t VALUES (2, 20) ON CONFLICT (id) DO UPDATE SET age = (SELECT 1)", "SUBQUERY in ON CONFLICT DO UPDATE")]
    [InlineData("INSERT INTO t SELECT id, age FROM t ON CONFLICT (id) DO UPDATE SET age = (SELECT 1)", "SUBQUERY in ON CONFLICT DO UPDATE")]
    [InlineData("INSERT INTO t VALUES (2, 20) ON CONFLICT (id) DO UPDATE SET age = 1 WHERE EXISTS (SELECT 1)", "SUBQUERY in ON CONFLICT DO UPDATE")]
    [InlineData("INSERT INTO t VALUES (2, 20) ON CONFLICT (id) DO UPDATE SET age = EXCLUDED.age RETURNING EXCLUDED.age", "EXCLUDED in RETURNING")]
    public async Task ExecuteAsync_ExcludedUpsert_ShouldKeepConnectionUsable(string sql, string construct)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, age INT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO t VALUES (1, 10)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        failure.Message.ShouldContain("COHDBL001", Case.Sensitive);
        failure.Message.ShouldContain(construct, Case.Sensitive);
        failure.ConnectionUsable.ShouldBeTrue();
        var rows = await connection.QueryAsync("SELECT id, age FROM t", cancellationToken: SqlClientTestHarness.Timeout());
        rows.ShouldHaveSingleItem().GetInt32(0).ShouldBe(1);
    }
}
