using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>Exercises executable subquery forms through SqlDatabaseServer and the production typed client.</summary>
public sealed class SqlSubqueryWireTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Client] - SELECT without FROM: constants and nested queries cross both transports (#1373)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_WithoutFrom_ShouldReturnImplicitRow(bool tcp)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var rows = await QueryAsync(connection, "SELECT (SELECT 42) AS answer WHERE 1 IN (SELECT 1) AND EXISTS (SELECT NULL)");
        var empty = await QueryAsync(connection, "SELECT 1 WHERE NULL");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(42);
        empty.ShouldBeEmpty();
        (await QueryAsync(connection, "SELECT COUNT(*) WHERE FALSE")).ShouldHaveSingleItem()[0].ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - WITH: query and all DML forms execute over the wire")]
    public async Task ExecuteAsync_CommonTables_ShouldReturnRowsAndExactAffectedCounts()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert
        (await QueryAsync(connection, "WITH picked AS (SELECT id FROM users) SELECT id FROM picked ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        (await ExecuteAsync(connection, "WITH picked AS (SELECT id + 10 AS id, name, score FROM users) INSERT INTO users SELECT * FROM picked")).ShouldBe(2);
        (await ExecuteAsync(connection, "INSERT INTO users WITH picked AS (SELECT id + 20 AS id, name, score FROM users WHERE id < 10) SELECT * FROM picked")).ShouldBe(2);
        (await ExecuteAsync(connection, "WITH picked AS (SELECT id FROM users WHERE id > 10) UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM picked)")).ShouldBe(4);
        (await ExecuteAsync(connection, "WITH picked AS (SELECT id FROM users WHERE name = 'changed') DELETE FROM users WHERE id IN (SELECT id FROM picked)")).ShouldBe(4);
        (await QueryAsync(connection, "SELECT id FROM users ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Derived tables: blank output names fail the statement and preserve the transaction")]
    [InlineData("SELECT * FROM (SELECT id AS \"\" FROM users) d")]
    [InlineData("SELECT * FROM (SELECT id FROM users) d (\" \")")]
    public async Task QueryAsync_BlankDerivedName_ShouldKeepConnectionUsable(string sql)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await ExecuteAsync(connection, "BEGIN TRANSACTION");
        await ExecuteAsync(connection, "UPDATE users SET name = 'pending' WHERE id = 1");

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(() => QueryAsync(connection, sql));

        // Assert
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.ConnectionUsable.ShouldBeTrue();
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05013");
        (await QueryAsync(connection, "SELECT name FROM users WHERE id = 1")).ShouldHaveSingleItem()[0].ShouldBe("pending");
        await ExecuteAsync(connection, "ROLLBACK");
        (await QueryAsync(connection, "SELECT name FROM users WHERE id = 1")).ShouldHaveSingleItem()[0].ShouldBe("ada");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Subquery: derived joins and DML predicates execute over the wire")]
    public async Task ExecuteAsync_DerivedAndDmlSubqueries_ShouldReturnRowsAndAffectedCounts()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var rows = await QueryAsync(connection, "SELECT u.id, d.label FROM users u JOIN (SELECT id, name FROM users WHERE id = 1) d (key_id, label) ON u.id = d.key_id");
        var updated = await ExecuteAsync(connection, "UPDATE users SET name = 'changed' WHERE id IN (SELECT id FROM users WHERE id = 1)");
        var deleted = await ExecuteAsync(connection, "DELETE FROM users WHERE EXISTS (SELECT id FROM users WHERE name = 'changed')");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(1);
        rows[0][1].ShouldBe("ada");
        updated.ShouldBe(1);
        deleted.ShouldBe(2);
        (await QueryAsync(connection, "SELECT id FROM users")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Subquery: DML scalar cardinality failure preserves rows and connection")]
    public async Task ExecuteAsync_DmlScalarCardinality_ShouldFailBeforeMutation()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var failure = await Should.ThrowAsync<SqlClientException>(() => ExecuteAsync(connection,
            "DELETE FROM users WHERE id = (SELECT id FROM users)"));

        // Assert
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        failure.ConnectionUsable.ShouldBeTrue();
        failure.Message.ShouldContain("more than one row");
        (await QueryAsync(connection, "SELECT id FROM users ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }

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
        string code = kind == "UNIQUE" ? "CS03037" : "CS03039";
        literalError.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        selectError.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        selectError.Message.ShouldContain(kind);
        selectError.Message.ShouldBe(literalError.Message);
        (await QueryAsync(connection, "SELECT id FROM target_rows")).ShouldBeEmpty();
    }

    /// <summary>Excluded forms have the capability diagnostic through the server rather than a late executor rejection.</summary>
    /// <param name="statement">The unsupported form.</param>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Client] - Subquery: excluded forms report CK00001 over the wire")]
    [InlineData("SELECT id FROM users WHERE id = ANY (1, 2)")]
    [InlineData("SELECT id FROM users WHERE id = ALL (1, 2)")]
    [InlineData("SELECT id FROM users WHERE id = SOME (1, 2)")]

    [InlineData("WITH RECURSIVE picked AS (SELECT id FROM users) SELECT id FROM picked")]
    [InlineData("WITH d AS (DELETE FROM users RETURNING *) SELECT * FROM d")]


    public async Task QueryAsync_ExcludedForm_ShouldReportCapabilityDiagnosticOverTheWire(string statement)
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act / Assert
        var error = await Should.ThrowAsync<SqlClientException>(() => QueryAsync(connection, statement));
        error.Kind.ShouldBe(SqlClientErrorKind.ParseFailure);
        error.Message.ShouldContain("CK00001", Case.Sensitive);
        (await QueryAsync(connection, "SELECT id FROM users ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }

    /// <summary>Unqualified names resolve to the enclosing scope and return its values over the wire.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Subquery: unqualified outer references execute over the wire")]
    public async Task QueryAsync_UnqualifiedCorrelation_ShouldReturnBoundOuterValuesOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await ExecuteAsync(connection, "CREATE TABLE inner_rows (id INT)");
        await ExecuteAsync(connection, "INSERT INTO inner_rows VALUES (100)");

        // Act / Assert
        (await QueryAsync(connection,
            "SELECT id FROM users WHERE EXISTS (SELECT id FROM inner_rows WHERE id = score)"))
            .ShouldHaveSingleItem()[0].ShouldBe(1);
        (await QueryAsync(connection, "SELECT id FROM users ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Correlation: EXISTS IN scalar and DML preserve per-row results and counts")]
    public async Task QueryAsync_CorrelatedSubqueries_ShouldExecuteEveryFormOverTheWire()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        await SeedAsync(connection);

        // Act / Assert
        var rows = await QueryAsync(connection, "SELECT c.id, (SELECT x.id FROM choices x WHERE x.id = c.id), " +
            "c.id IN (SELECT x.id FROM choices x WHERE x.id = c.id) FROM candidates c " +
            "WHERE EXISTS (SELECT x.id FROM choices x WHERE x.id = c.id)");
        rows.ShouldHaveSingleItem()[0].ShouldBe(2);
        rows[0][1].ShouldBe(2);
        rows[0][2].ShouldBe(true);
        (await ExecuteAsync(connection, "UPDATE candidates SET id = id + 10 WHERE EXISTS " +
            "(SELECT x.id FROM choices x WHERE x.id = candidates.id)" )).ShouldBe(1L);
        (await ExecuteAsync(connection, "DELETE FROM candidates WHERE id IN " +
            "(SELECT x.id + 10 FROM choices x WHERE x.id + 10 = candidates.id)" )).ShouldBe(1L);
        (await QueryAsync(connection, "SELECT id FROM candidates ORDER BY id")).Select(row => row[0])
            .ShouldBe(new object?[] { null, 1, 3 });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - Correlation: a later outer row cardinality failure keeps the connection usable")]
    public async Task QueryAsync_CorrelatedScalarCardinality_ShouldReturnExecutionFailureAndKeepConnectionUsable()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(() => QueryAsync(connection,
            "SELECT u.id, (SELECT v.id FROM users v WHERE v.id = u.id OR u.id = 2) FROM users u ORDER BY u.id"));

        // Assert
        error.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
        error.ConnectionUsable.ShouldBeTrue();
        error.Message.ShouldContain("one row");
        (await QueryAsync(connection, "SELECT id FROM users ORDER BY id")).Select(row => row[0])
            .ShouldBe(new object?[] { 1, 2 });
    }

    private static async Task SeedAsync(SqlConnection connection)
    {
        await ExecuteAsync(connection, "CREATE TABLE candidates (id INT)");
        await ExecuteAsync(connection, "CREATE TABLE choices (id INT)");
        await ExecuteAsync(connection, "INSERT INTO candidates VALUES (1), (2), (3), (NULL)");
        await ExecuteAsync(connection, "INSERT INTO choices VALUES (2), (NULL)");
    }

    private static Task<long> ExecuteAsync(SqlConnection connection, string sql)
        => connection.ExecuteAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()).AsTask();

    private static Task<SqlResultSet> QueryAsync(SqlConnection connection, string sql)
        => connection.QueryAsync(sql, cancellationToken: SqlClientTestHarness.Timeout()).AsTask();
}
