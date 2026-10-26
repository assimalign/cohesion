using System;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// Exercises JOIN through encoded SQL exchanges between SqlDatabaseServer and
/// the production typed SQL client, including its error mapping and metadata.
/// </summary>
public sealed class SqlJoinWireTests
{
    /// <summary>The owner's exact schema-qualified query returns typed matching rows over the wire.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - JOIN: owner query executes over the wire")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_OwnerJoin_ShouldReturnMatchingPairsOverTheWire(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act: preserve the motivating query, which deliberately has no ORDER BY.
        var result = await connection.QueryAsync(
            "SELECT usr.Users.FirstName, usr.Users.LastName, usr.UsersProfile.Email " +
            "FROM usr.Users INNER JOIN usr.UsersProfile ON usr.Users.Id = usr.UsersProfile.UserId",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert: compare the complete set independently of physical iteration order.
        result.Columns.Select(column => column.Name).ShouldBe(["FirstName", "LastName", "Email"]);
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.String, DatabaseType.String, DatabaseType.String]);
        result.Select(row => $"{row[0]}|{row[1]}|{row[2]}").OrderBy(value => value, StringComparer.Ordinal).ShouldBe(
            ["Ada|Lovelace|ada-alt@example.test", "Ada|Lovelace|ada@example.test", "Grace|Hopper|grace@example.test"]);
        result.SelectMany(row => new[] { row[0], row[1], row[2] }).ShouldAllBe(value => value is string);
    }

    /// <summary>Aliased joins retain filter, sort, and pagination semantics through the client.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - JOIN: filters ordering and pagination survive the wire")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_JoinClauses_ShouldComposeOverTheWire(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);
        var command = new SqlCommand(
            "SELECT u.FirstName, p.Email FROM usr.Users u JOIN usr.UsersProfile p ON u.Id = p.UserId " +
            "WHERE u.Id >= @minimum AND p.Email LIKE '%example.test' ORDER BY u.Id DESC, p.Email ASC LIMIT 1 OFFSET 1")
            .WithParameter("minimum", 1);

        // Act
        var result = await connection.QueryAsync(command, SqlClientTestHarness.Timeout());

        // Assert
        var row = result.ShouldHaveSingleItem();
        row["FirstName"].ShouldBe("Ada");
        row["Email"].ShouldBe("ada-alt@example.test");
    }

    /// <summary>A nonmatching or empty joined side returns a successful typed empty result.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - JOIN: no matches and empty inputs return no rows over the wire")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_NoMatchesAndEmptySide_ShouldReturnEmptyResultsOverTheWire(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act / Assert
        var nonmatching = await connection.QueryAsync(
            "SELECT u.FirstName, p.Email FROM usr.Users u JOIN usr.UsersProfile p ON u.Id = p.UserId WHERE u.Id = 3",
            cancellationToken: SqlClientTestHarness.Timeout());
        nonmatching.ShouldBeEmpty();
        nonmatching.Columns.Select(column => column.Type).ShouldBe([DatabaseType.String, DatabaseType.String]);
        await connection.ExecuteAsync("DELETE FROM usr.UsersProfile", cancellationToken: SqlClientTestHarness.Timeout());
        var empty = await connection.QueryAsync(
            "SELECT u.FirstName, p.Email FROM usr.Users u JOIN usr.UsersProfile p ON u.Id = p.UserId",
            cancellationToken: SqlClientTestHarness.Timeout());
        empty.ShouldBeEmpty();
    }

    /// <summary>Unimplemented join forms produce the capability diagnostic before planning over the wire.</summary>
    /// <param name="statement">The excluded join form.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - JOIN: excluded forms report CK00001 over the wire")]
    [InlineData(false, "SELECT u.id FROM users u FULL OUTER JOIN users v ON u.id = v.id")]
    [InlineData(true, "SELECT u.id FROM users u FULL OUTER JOIN users v ON u.id = v.id")]
    [InlineData(false, "SELECT u.id FROM users u NATURAL JOIN users v")]
    [InlineData(true, "SELECT u.id FROM users u NATURAL JOIN users v")]
    [InlineData(false, "SELECT u.* FROM users u JOIN users v USING (id)")]
    [InlineData(true, "SELECT u.* FROM users u JOIN users v USING (id)")]
    [InlineData(false, "SELECT u.* FROM users u LEFT JOIN users v USING (id)")]
    [InlineData(true, "SELECT u.* FROM users u LEFT JOIN users v USING (id)")]
    public async Task QueryAsync_ExcludedJoin_ShouldReportCapabilityDiagnosticOverTheWire(bool tcp, string statement)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync(statement, cancellationToken: SqlClientTestHarness.Timeout()));
        error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        error.Message.ShouldContain("CK00001", Case.Sensitive);
        (await connection.QueryAsync("SELECT u.id FROM users u JOIN users v ON u.id = v.id ORDER BY u.id",
            cancellationToken: SqlClientTestHarness.Timeout())).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }

    /// <summary>Ambiguous binding errors remain explicit and leave the client connection usable.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - JOIN: ambiguous names report execution errors over the wire")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_AmbiguousJoinColumn_ShouldReportBindingErrorOverTheWire(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(
            "SELECT id FROM users u JOIN users v ON u.id = v.id", cancellationToken: SqlClientTestHarness.Timeout()));
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Message.ShouldContain("ambiguous");
        (await connection.QueryAsync("SELECT u.id FROM users u JOIN users v ON u.id = v.id WHERE u.id = 1",
            cancellationToken: SqlClientTestHarness.Timeout())).ShouldHaveSingleItem()[0].ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - JOIN: system relations compose with stored and grouped inputs over the wire")]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task QueryAsync_SystemJoin_ShouldReturnExactTypedRowsOverTheWire(bool tcp, int shape)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE inventory_names (name TEXT)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO inventory_names VALUES ('inventory_names')", cancellationToken: SqlClientTestHarness.Timeout());
        string query = shape switch
        {
            0 => "SELECT t.TABLE_NAME, c.ORDINAL_POSITION FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c " +
                "ON t.TABLE_NAME = c.TABLE_NAME WHERE t.TABLE_NAME = 'inventory_names' ORDER BY c.ORDINAL_POSITION",
            1 => "SELECT t.TABLE_NAME, 1 AS n FROM inventory_names n JOIN INFORMATION_SCHEMA.TABLES t ON n.name = t.TABLE_NAME",
            _ => "SELECT t.TABLE_NAME, COUNT(*) AS n FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c " +
                "ON t.TABLE_NAME = c.TABLE_NAME WHERE t.TABLE_NAME = 'inventory_names' GROUP BY t.TABLE_NAME HAVING COUNT(*) = 1 ORDER BY t.TABLE_NAME",
        };

        // Act
        var result = await connection.QueryAsync(query, cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.String, DatabaseType.Int64]);
        var row = result.ShouldHaveSingleItem();
        row[0].ShouldBe("inventory_names");
        row[1].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - JOIN: LEFT, RIGHT, CROSS, comma, USING and N-way execute on production frames")]
    [InlineData(false, "SELECT u.id, v.id FROM users u RIGHT OUTER JOIN users v ON u.id = v.id AND u.id = 1 ORDER BY v.id", "1|1;|2")]
    [InlineData(true, "SELECT u.id, v.id FROM users u RIGHT OUTER JOIN users v ON u.id = v.id AND u.id = 1 ORDER BY v.id", "1|1;|2")]
    [InlineData(false, "SELECT u.id, v.id FROM users u LEFT JOIN users v ON u.id = v.id AND v.id = 1 ORDER BY u.id", "1|1;2|")]
    [InlineData(true, "SELECT u.id, v.id FROM users u LEFT JOIN users v ON u.id = v.id AND v.id = 1 ORDER BY u.id", "1|1;2|")]
    [InlineData(false, "SELECT u.id, v.id FROM users u CROSS JOIN users v ORDER BY u.id, v.id", "1|1;1|2;2|1;2|2")]
    [InlineData(true, "SELECT u.id, v.id FROM users u CROSS JOIN users v ORDER BY u.id, v.id", "1|1;1|2;2|1;2|2")]
    [InlineData(false, "SELECT u.id, v.id FROM users u, users v ORDER BY u.id, v.id", "1|1;1|2;2|1;2|2")]
    [InlineData(true, "SELECT u.id, v.id FROM users u, users v ORDER BY u.id, v.id", "1|1;1|2;2|1;2|2")]
    [InlineData(false, "SELECT id, v.id FROM users u LEFT JOIN users v USING (id) ORDER BY id", "1|1;2|2")]
    [InlineData(true, "SELECT id, v.id FROM users u LEFT JOIN users v USING (id) ORDER BY id", "1|1;2|2")]
    [InlineData(false, "SELECT u.id, w.id FROM users u JOIN users v ON u.id = v.id LEFT JOIN users w ON v.id = w.id AND w.id = 1 ORDER BY u.id", "1|1;2|")]
    [InlineData(true, "SELECT u.id, w.id FROM users u JOIN users v ON u.id = v.id LEFT JOIN users w ON v.id = w.id AND w.id = 1 ORDER BY u.id", "1|1;2|")]
    [InlineData(false, "SELECT u.id, t.TABLE_NAME FROM users u LEFT JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_NAME = 'absent' ORDER BY u.id", "1|;2|")]
    [InlineData(true, "SELECT u.id, t.TABLE_NAME FROM users u LEFT JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_NAME = 'absent' ORDER BY u.id", "1|;2|")]
    public async Task QueryAsync_ExpandedJoins_ShouldReturnExactWireValues(bool tcp, string sql, string expected)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var result = await connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        string.Join(";", result.Select(row => $"{row[0]}|{row[1]}")).ShouldBe(expected);
        result.Columns[0].Type.ShouldBe(DatabaseType.Int32);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - RIGHT USING retains written slots, merged right values and coded DML refusal (#1525)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_RightJoin_ShouldPreserveWrittenOutputs(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        foreach (string sql in new[]
        {
            "CREATE TABLE a (extra TEXT, id DECIMAL NOT NULL, name VARCHAR(8) COLLATE case_insensitive)",
            "CREATE TABLE b (id INT NOT NULL, name VARCHAR(12) COLLATE case_insensitive, tail TEXT)",
            "INSERT INTO a VALUES ('L', 1, 'Ada')",
            "INSERT INTO b VALUES (1, 'ADA', 'R'), (2, 'Grace', 'unmatched')",
        })
        {
            await connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout());
        }

        // Act
        var result = await connection.QueryAsync("SELECT *, a.id, b.id FROM a RIGHT JOIN b USING(name,id) ORDER BY id",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.Select(column => column.Name).ShouldBe(new[] { "name", "id", "extra", "tail", "id", "id" });
        result.Columns.Select(column => column.Type).ShouldBe(new[] { DatabaseType.String, DatabaseType.Decimal, DatabaseType.String,
            DatabaseType.String, DatabaseType.Decimal, DatabaseType.Int32 });
        result.Columns[0].MaxLength.ShouldBeNull();
        result.Count.ShouldBe(2);
        Enumerable.Range(0, 6).Select(index => result[0][index]).ShouldBe(new object?[] { "ADA", 1m, "L", "R", 1m, 1 });
        Enumerable.Range(0, 6).Select(index => result[1][index]).ShouldBe(new object?[] { "Grace", 2m, null, "unmatched", null, 2 });
        var refusal = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
            "UPDATE a SET id = 9 FROM a x RIGHT JOIN b ON x.id = b.id", cancellationToken: SqlClientTestHarness.Timeout()));
        refusal.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        refusal.Message.ShouldContain("CS05069", Case.Sensitive);
        (await connection.QueryAsync("SELECT id FROM a", cancellationToken: SqlClientTestHarness.Timeout())).ShouldHaveSingleItem()[0].ShouldBe(1m);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - USING: wildcard aliases and ordinals use visible wire outputs")]
    [InlineData(false, "JOIN")]
    [InlineData(true, "JOIN")]
    [InlineData(false, "LEFT JOIN")]
    [InlineData(true, "LEFT JOIN")]
    public async Task QueryAsync_UsingWildcardOrdering_ShouldReturnVisibleAliasValues(bool tcp, string join)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        string from = $" FROM users u {join} users v USING (id) ";

        // Act
        var result = await connection.QueryAsync("SELECT *, u.id AS sort, -u.id AS tail" + from + "ORDER BY sort",
            cancellationToken: SqlClientTestHarness.Timeout());
        var single = await connection.QueryAsync("SELECT *, u.id AS sort" + from + "ORDER BY sort",
            cancellationToken: SqlClientTestHarness.Timeout());
        var ordinal = await connection.QueryAsync("SELECT *, -u.id AS sort" + from + "ORDER BY 6",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        result.Select(row => row[5]).ShouldBe(new object?[] { 1, 2 });
        result.Select(row => row[6]).ShouldBe(new object?[] { -1L, -2L });
        single.Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        ordinal.Select(row => row[0]).ShouldBe(new object?[] { 2, 1 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - USING: common numeric types and cleared character lengths survive both transports")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task QueryAsync_UsingCommonTypes_ShouldReturnCoercedWireValues(bool tcp, bool reverse)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE a (id INT NOT NULL)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE b (id DECIMAL NOT NULL)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO a VALUES (1)", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO b VALUES (1)", cancellationToken: SqlClientTestHarness.Timeout());
        string left = reverse ? "b" : "a", right = reverse ? "a" : "b";
        await connection.ExecuteAsync($"INSERT INTO {left} VALUES (3)", cancellationToken: SqlClientTestHarness.Timeout());

        // Act
        var result = await connection.QueryAsync($"SELECT *, id / 2 AS ratio, a.id, b.id FROM {left} LEFT JOIN {right} USING (id) ORDER BY ratio",
            cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Int32, DatabaseType.Decimal]);
        result.Select(row => row[0]).ShouldBe(new object?[] { 1m, 3m });
        result.Select(row => row[1]).ShouldBe(new object?[] { 0.5m, 1.5m });
        result[1][reverse ? 2 : 3].ShouldBeNull();

        // Arrange / Act: qualified character lengths remain distinct from the merged modifier.
        await connection.ExecuteAsync("CREATE TABLE c (id CHAR(3))", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE d (id VARCHAR(10))", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO c VALUES ('a')", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO d VALUES ('a')", cancellationToken: SqlClientTestHarness.Timeout());
        var characters = await connection.QueryAsync("SELECT *, c.id, d.id FROM " +
            (reverse ? "d LEFT JOIN c" : "c LEFT JOIN d") + " USING (id)", cancellationToken: SqlClientTestHarness.Timeout());

        // Assert
        characters.Columns.Select(column => column.MaxLength).ShouldBe(new int?[] { null, 3, 10 });
        characters.ShouldHaveSingleItem()[0].ShouldBe(reverse ? "a" : "a  ");
    }

    private static async Task SeedAsync(SqlConnection connection)
    {
        await connection.ExecuteAsync("CREATE SCHEMA usr", cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE usr.Users (Id INT PRIMARY KEY, FirstName TEXT, LastName TEXT)",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE TABLE usr.UsersProfile (Id INT PRIMARY KEY, UserId INT REFERENCES usr.Users(Id), Email TEXT)",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("CREATE INDEX ix_profile_user ON usr.UsersProfile(UserId)",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO usr.Users VALUES (1, 'Ada', 'Lovelace'), (2, 'Grace', 'Hopper'), (3, 'Alan', 'Turing')",
            cancellationToken: SqlClientTestHarness.Timeout());
        await connection.ExecuteAsync("INSERT INTO usr.UsersProfile VALUES (10, 1, 'ada@example.test'), (11, 1, 'ada-alt@example.test'), (20, 2, 'grace@example.test'), (90, NULL, 'unlinked@example.test')",
            cancellationToken: SqlClientTestHarness.Timeout());
    }
}
