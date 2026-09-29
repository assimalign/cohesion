using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>Exercises executable subquery forms through SqlDatabaseServer and the production typed client.</summary>
public sealed class SqlSubqueryWireTests
{
    /// <summary>Wire execution preserves membership matches and the NOT IN null trap.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Subquery: IN NOT IN nulls and empty sources execute over the wire")]
    public async Task QueryAsync_Membership_ShouldPreserveNullAndEmptySemanticsOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act / Assert
        (await QueryAsync(connection, "SELECT id FROM candidates WHERE id IN (SELECT id FROM choices) ORDER BY id"))
            .Select(row => row[0]).ShouldBe(new object?[] { 2 });
        (await QueryAsync(connection, "SELECT id FROM candidates WHERE id NOT IN (SELECT id FROM choices WHERE id IS NOT NULL) ORDER BY id"))
            .Select(row => row[0]).ShouldBe(new object?[] { 1, 3 });
        (await QueryAsync(connection, "SELECT id FROM candidates WHERE id NOT IN (SELECT id FROM choices)"))
            .ShouldBeEmpty();
        (await QueryAsync(connection, "SELECT id FROM candidates WHERE id IN (SELECT id FROM choices WHERE id < 0)"))
            .ShouldBeEmpty();
        (await QueryAsync(connection, "SELECT id FROM candidates WHERE id NOT IN (SELECT id FROM choices WHERE id < 0) ORDER BY id"))
            .Select(row => row[0]).ShouldBe(new object?[] { null, 1, 2, 3 });
        var unknown = await QueryAsync(connection,
            "SELECT id IN (SELECT id FROM choices), id NOT IN (SELECT id FROM choices) FROM candidates WHERE id = 1");
        unknown.ShouldHaveSingleItem()[0].ShouldBeNull();
        unknown[0][1].ShouldBeNull();
    }

    /// <summary>Both existence flags execute with nonempty, null-only and empty sources.</summary>
    /// <param name="predicate">The existence expression.</param>
    /// <param name="count">The expected number of outer rows.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Subquery: EXISTS and NOT EXISTS execute over the wire")]
    [InlineData("EXISTS (SELECT id FROM choices)", 4)]
    [InlineData("NOT EXISTS (SELECT id FROM choices)", 0)]
    [InlineData("EXISTS (SELECT id FROM choices WHERE id IS NULL)", 4)]
    [InlineData("EXISTS (SELECT id FROM choices WHERE id < 0)", 0)]
    [InlineData("NOT EXISTS (SELECT id FROM choices WHERE id < 0)", 4)]
    public async Task QueryAsync_Existence_ShouldReturnCorrectRowsOverTheWire(string predicate, int count)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act / Assert
        (await QueryAsync(connection, $"SELECT id FROM candidates WHERE {predicate}")).Count.ShouldBe(count);
    }

    /// <summary>Scalar projections retain wire metadata and nested predicate values, including empty-source nulls.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Subquery: scalar projection predicate nesting and empty nulls survive the wire")]
    public async Task QueryAsync_Scalar_ShouldReturnTypedValuesAndNullsOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act
        var result = await QueryAsync(connection,
            "SELECT id, (SELECT id FROM choices WHERE id IS NOT NULL) AS picked, " +
            "(SELECT id FROM choices WHERE id < 0) AS absent FROM candidates " +
            "WHERE id = (SELECT MAX(id) FROM choices WHERE id IN (SELECT id FROM candidates))");

        // Assert
        result.Columns.Select(column => column.Name).ShouldBe(["id", "picked", "absent"]);
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Int32, DatabaseType.Int32, DatabaseType.Int32]);
        result.ShouldHaveSingleItem()[0].ShouldBe(2);
        result[0][1].ShouldBe(2);
        result[0][2].ShouldBeNull();
        (await QueryAsync(connection, "SELECT id FROM candidates WHERE id = (SELECT id FROM choices WHERE id < 0)"))
            .ShouldBeEmpty();
    }

    /// <summary>Scalar cardinality failures map to execution failures while leaving the connection usable.</summary>
    /// <param name="statement">The scalar expression position.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Subquery: multiple scalar rows fail over the wire")]
    [InlineData("SELECT (SELECT id FROM choices) FROM candidates WHERE id = 1")]
    [InlineData("SELECT id FROM candidates WHERE id = (SELECT id FROM choices)")]
    public async Task QueryAsync_MultipleScalarRows_ShouldReportExecutionFailureOverTheWire(string statement)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(() => QueryAsync(connection, statement));
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Message.ShouldContain("scalar");
        error.Message.ShouldContain("one row");
        (await QueryAsync(connection, "SELECT id FROM choices WHERE id IS NOT NULL")).ShouldHaveSingleItem()[0].ShouldBe(2);
    }

    /// <summary>Subqueries combine with joins, grouping, HAVING, ordering, LIMIT and parameters over encoded exchanges.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Subquery: joined and grouped plans compose over the wire")]
    public async Task QueryAsync_ComposedSubquery_ShouldPreserveGroupingAndJoinResultsOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await ExecuteAsync(connection, "CREATE TABLE sales (id INT, category TEXT, amount INT)");
        await ExecuteAsync(connection, "CREATE TABLE labels (id INT, category TEXT)");
        await ExecuteAsync(connection, "INSERT INTO sales VALUES (1, 'A', 4), (2, 'A', 6), (3, 'B', 8), (4, 'C', 3)");
        await ExecuteAsync(connection, "INSERT INTO labels VALUES (1, 'A'), (2, 'B')");
        var command = new SqlCommand(
            "SELECT s.category, SUM(s.amount) AS total FROM sales s JOIN labels l ON s.category = l.category " +
            "AND l.id IN (SELECT id FROM labels WHERE id >= @minimum) " +
            "WHERE EXISTS (SELECT id FROM labels) GROUP BY s.category " +
            "HAVING SUM(s.amount) > (SELECT MIN(amount) FROM sales WHERE category = 'B') " +
            "ORDER BY SUM(s.amount) DESC LIMIT 1").WithParameter("minimum", 1);

        // Act
        var result = await connection.QueryAsync(command, SqlClientTestHarness.Timeout());

        // Assert
        result.ShouldHaveSingleItem()["category"].ShouldBe("A");
        result[0]["total"].ShouldBe(10m);
    }

    /// <summary>INSERT SELECT reports inserted counts, honors defaults and safely copies from its own target.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - INSERT SELECT: rows defaults self-copy and affected counts survive the wire")]
    public async Task ExecuteAsync_InsertSelect_ShouldInsertSelectedRowsOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);
        await ExecuteAsync(connection, "CREATE TABLE copied (id INT PRIMARY KEY, label TEXT NOT NULL DEFAULT 'copied')");

        // Act / Assert
        (await ExecuteAsync(connection,
            "INSERT INTO copied (id) SELECT id FROM candidates WHERE id IN (SELECT id FROM choices)"))
            .ShouldBe(1L);
        (await ExecuteAsync(connection, "INSERT INTO copied (id) SELECT id + 10 FROM copied")).ShouldBe(1L);
        (await ExecuteAsync(connection, "INSERT INTO copied (id) SELECT id FROM candidates WHERE id < 0")).ShouldBe(0L);
        var result = await QueryAsync(connection, "SELECT id, label FROM copied ORDER BY id");
        result.Select(row => row[0]).ShouldBe(new object?[] { 2, 12 });
        result.Select(row => row[1]).ShouldBe(new object?[] { "copied", "copied" });
    }

    /// <summary>Constraint failures are identical for literal and query inserts and never publish a partial batch.</summary>
    /// <param name="definition">The target constraint.</param>
    /// <param name="values">The valid row followed by the invalid row.</param>
    /// <param name="kind">The expected error text.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - INSERT SELECT: UNIQUE and foreign-key violations match literal inserts over the wire")]
    [InlineData("id INT UNIQUE", "(1), (1)", "UNIQUE")]
    [InlineData("id INT REFERENCES parents(id)", "(1), (99)", "FOREIGN KEY")]
    public async Task ExecuteAsync_InsertSelectConstraintViolation_ShouldRemainAtomicOverTheWire(
        string definition, string values, string kind)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await ExecuteAsync(connection, "CREATE TABLE parents (id INT PRIMARY KEY)");
        await ExecuteAsync(connection, "INSERT INTO parents VALUES (1)");
        await ExecuteAsync(connection, "CREATE TABLE source_rows (id INT)");
        await ExecuteAsync(connection, $"CREATE TABLE target_rows ({definition})");
        await ExecuteAsync(connection, $"INSERT INTO source_rows VALUES {values}");

        // Act / Assert
        var literalError = await Should.ThrowAsync<SqlClientException>(
            () => ExecuteAsync(connection, $"INSERT INTO target_rows VALUES {values}"));
        var selectError = await Should.ThrowAsync<SqlClientException>(
            () => ExecuteAsync(connection, "INSERT INTO target_rows SELECT id FROM source_rows"));
        literalError.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        selectError.Kind.ShouldBe(literalError.Kind);
        selectError.Message.ShouldContain(kind);
        selectError.Message.ShouldBe(literalError.Message);
        (await QueryAsync(connection, "SELECT id FROM target_rows")).ShouldBeEmpty();
    }

    /// <summary>Excluded forms have the capability diagnostic through the server rather than a late executor rejection.</summary>
    /// <param name="statement">The unsupported form.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Subquery: excluded forms report COHDBL001 over the wire")]
    [InlineData("SELECT u.id FROM users u WHERE EXISTS (SELECT v.id FROM users v WHERE v.id = u.id)")]
    [InlineData("SELECT id FROM users WHERE id = ANY (SELECT id FROM users)")]
    [InlineData("SELECT id FROM users WHERE id = ALL (SELECT id FROM users)")]
    [InlineData("SELECT id FROM users WHERE id = SOME (SELECT id FROM users)")]
    [InlineData("SELECT d.id FROM (SELECT id FROM users) d")]
    [InlineData("WITH picked AS (SELECT id FROM users) SELECT id FROM picked")]
    [InlineData("UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM users)")]
    [InlineData("DELETE FROM users WHERE EXISTS (SELECT id FROM users)")]
    public async Task QueryAsync_ExcludedForm_ShouldReportCapabilityDiagnosticOverTheWire(string statement)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(() => QueryAsync(connection, statement));
        error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        error.Message.ShouldContain("COHDBL001", Case.Sensitive);
        (await QueryAsync(connection, "SELECT id FROM users ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }

    /// <summary>Unqualified names that require an outer scope receive the same capability diagnostic as qualified correlation.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Subquery: unqualified outer references report COHDBL001 over the wire")]
    public async Task QueryAsync_UnqualifiedCorrelation_ShouldReportCapabilityDiagnosticOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await ExecuteAsync(connection, "CREATE TABLE inner_rows (id INT)");
        await ExecuteAsync(connection, "INSERT INTO inner_rows VALUES (1)");

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(() => QueryAsync(connection,
            "SELECT id FROM users WHERE EXISTS (SELECT id FROM inner_rows WHERE id = score)"));
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.Message.ShouldContain("COHDBL001", Case.Sensitive);
        error.Message.ShouldContain("correlat");
        (await QueryAsync(connection, "SELECT id FROM users ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }

    private static async Task SeedAsync(ISqlConnection connection)
    {
        await ExecuteAsync(connection, "CREATE TABLE candidates (id INT)");
        await ExecuteAsync(connection, "CREATE TABLE choices (id INT)");
        await ExecuteAsync(connection, "INSERT INTO candidates VALUES (1), (2), (3), (NULL)");
        await ExecuteAsync(connection, "INSERT INTO choices VALUES (2), (NULL)");
    }

    private static Task<long> ExecuteAsync(ISqlConnection connection, string sql)
        => connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()).AsTask();

    private static Task<SqlResultSet> QueryAsync(ISqlConnection connection, string sql)
        => connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()).AsTask();
}
