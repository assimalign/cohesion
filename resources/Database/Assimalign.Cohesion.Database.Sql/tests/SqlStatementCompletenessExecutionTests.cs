using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Measures statement completeness (#1068) against the live engine: a statement with
/// text the parser did not consume never executes, and an unknown function fails at
/// plan time, before a row is read, whatever the table holds.
/// </summary>
public sealed class SqlStatementCompletenessExecutionTests
{
    private static readonly string[] _schema =
    [
        "CREATE TABLE t (id INT PRIMARY KEY, name TEXT, age INT, flag BOOLEAN);",
        "CREATE TABLE u (id INT PRIMARY KEY, name TEXT);",
    ];

    private static readonly string[] _rows =
    [
        "INSERT INTO t VALUES (1, 'ann', 36, TRUE), (2, 'a%b', 45, NULL), (3, 'bob', 41, FALSE);",
        "INSERT INTO u VALUES (1, 'x'), (4, 'y');",
    ];

    /// <summary>
    /// Each statement used to execute a truncated prefix; every one of them would change
    /// data if that prefix ran. Both session seams refuse it and the tables are unchanged.
    /// </summary>
    /// <param name="sql">The statement whose tail the parser used to drop.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Completeness: a statement with leftover text reports SQL0003 and changes nothing")]
    [InlineData("DELETE FROM t WHRE id = 1;")]
    [InlineData("UPDATE t SET name = 'z' WHRE id = 1;")]
    [InlineData("UPDATE t SET name = u.name FROM u WHERE t.id = u.id;")]
    [InlineData("INSERT INTO t (id, name, age) VALUES (5, 'new', 1) ON CONFLICT DO NOTHING;")]
    [InlineData("INSERT INTO t (id, name, age) VALUES (5, 'new', 1) ON DUPLICATE KEY UPDATE name = 'dup';")]
    [InlineData("SELECT id FROM t ORDER BY id OFFSET 1 LIMIT 2;")]
    [InlineData("SELECT id FROM t WHERE name LIKE 'a!%' ESCAPE '!';")]
    [InlineData("DELETE FROM t WHERE name LIKE 'a%' ESCAPE '!';")]
    [InlineData("DELETE FROM t WHERE flag IS DISTINCT FROM TRUE;")]
    [InlineData("DELETE FROM t WHERE flag IS TRUE;")]
    [InlineData("DELETE FROM t WHERE id = :id;")]
    [InlineData("UPDATE t SET name = :name WHERE id = 1;")]
    [InlineData("DELETE FROM t WHERE id = 1; DELETE FROM t;")]
    [InlineData("BEGIN; DELETE FROM t;")]
    public async Task ExecuteAsync_LeftoverText_ShouldReportSyntaxErrorWithoutMutation(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-completeness" });
        var database = await engine.CreateDatabaseAsync("completeness");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session, withRows: true);
        var before = await SnapshotAsync(session);

        // Act
        var textError = await Should.ThrowAsync<DatabaseParseException>(() => ExecuteAsync(session, sql));
        var parsed = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        var typed = await session.ExecuteAsync(new SqlQueryRequest(parsed), CancellationToken.None);

        // Assert
        textError.Message.ShouldContain("SQL0003", Case.Sensitive);
        typed.Status.ShouldBe(QueryResultStatus.Error);
        typed.Diagnostics.ShouldNotBeNull().ShouldContain(diagnostic => diagnostic.Code == "SQL0003" && diagnostic.Severity == DiagnosticSeverity.Error);
        session.CurrentTransaction.ShouldBeNull();
        (await SnapshotAsync(session)).ShouldBe(before);
    }

    /// <summary>An unsupported ALTER TABLE action is named at parse time and the table is untouched.</summary>
    /// <param name="sql">The ALTER TABLE statement.</param>
    /// <param name="action">The action the error names.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Completeness: an unsupported ALTER TABLE action fails at parse time")]
    [InlineData("ALTER TABLE t RENAME TO v;", "RENAME TO")]
    [InlineData("ALTER TABLE t ALTER COLUMN age TYPE BIGINT;", "ALTER COLUMN")]
    [InlineData("ALTER TABLE t;", "requires an action")]
    public async Task ExecuteAsync_UnsupportedAlterTableAction_ShouldFailAtParseTime(string sql, string action)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-completeness-alter" });
        var database = await engine.CreateDatabaseAsync("completeness");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session, withRows: true);
        var before = await SnapshotAsync(session);

        // Act
        var error = await Should.ThrowAsync<DatabaseParseException>(() => ExecuteAsync(session, sql));

        // Assert
        error.Message.ShouldContain("SQL0003", Case.Sensitive);
        error.Message.ShouldContain(action, Case.Sensitive);
        (await SnapshotAsync(session)).ShouldBe(before);
        (await RowsAsync(session, "SELECT name, age FROM t WHERE id = 1;")).ShouldHaveSingleItem().ShouldBe(new object?[] { "ann", 36 });
    }

    /// <summary>
    /// An unknown function is rejected for the whole statement before a row is read, so
    /// the outcome no longer depends on whether the table has rows.
    /// </summary>
    /// <param name="sql">A statement that calls an unknown function somewhere.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Functions: an unknown function fails at plan time over empty and populated tables")]
    [InlineData("SELECT FOO(id) FROM t;")]
    [InlineData("SELECT UPPER(FOO(name)) FROM t;")]
    [InlineData("SELECT id FROM t WHERE FOO(id) = 1;")]
    [InlineData("SELECT id FROM t ORDER BY FOO(id);")]
    [InlineData("SELECT FOO(age), COUNT(*) FROM t GROUP BY FOO(age);")]
    [InlineData("SELECT age FROM t GROUP BY age HAVING FOO(COUNT(*)) > 1;")]
    [InlineData("SELECT COUNT(FOO(id)) FROM t;")]
    [InlineData("SELECT t.id FROM t JOIN u ON FOO(t.id) = u.id;")]
    [InlineData("SELECT id FROM t WHERE id IN (SELECT FOO(id) FROM u);")]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT id FROM u WHERE FOO(id) = 1);")]
    [InlineData("SELECT (SELECT FOO(id) FROM u) FROM t;")]
    [InlineData("SELECT id FROM t LIMIT FOO(1);")]
    [InlineData("UPDATE t SET name = FOO(name);")]
    [InlineData("UPDATE t SET name = 'z' WHERE FOO(id) = 1;")]
    [InlineData("DELETE FROM t WHERE FOO(id) = 1;")]
    [InlineData("INSERT INTO t (id, name) VALUES (9, FOO('x'));")]
    [InlineData("INSERT INTO t (id, name) SELECT id, FOO(name) FROM u;")]
    [InlineData("CREATE TABLE c (a INT CHECK (FOO(a) > 0));")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT ck CHECK (FOO(age) > 0);")]
    public async Task ExecuteAsync_UnknownFunction_ShouldFailAtPlanTimeWhateverTheRows(string sql)
    {
        foreach (bool withRows in new[] { false, true })
        {
            // Arrange
            await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-completeness-functions" });
            var database = await engine.CreateDatabaseAsync("completeness");
            await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
            await SeedAsync(session, withRows);
            var before = await SnapshotAsync(session);
            new SqlQueryParser().Parse(sql).Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

            // Act
            var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));

            // Assert
            error.ShouldNotBeOfType<DatabaseParseException>();
            error.Message.ShouldBe("Unknown function 'FOO'.", $"withRows: {withRows}");
            (await SnapshotAsync(session)).ShouldBe(before);
            ((SqlDatabaseInstance)session.Database).Catalog.TryGetTable("dbo", "c", out _).ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: the unknown-function error keeps the written spelling")]
    public async Task ExecuteAsync_LowercaseUnknownFunction_ShouldNameItAsWritten()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-completeness-spelling" });
        var database = await engine.CreateDatabaseAsync("completeness");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session, withRows: false);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "select foo_bar(id) from t;"));

        // Assert
        error.Message.ShouldBe("Unknown function 'foo_bar'.");
    }

    /// <summary>
    /// Pins the boundary with #1103: a declared name is not unknown, so this check leaves
    /// it to evaluation. #1103 rejects declared-but-non-executing names at parse time
    /// with COHDBL001 and flips this pin in the same change.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Functions: a declared name that does not execute is not an unknown function")]
    public async Task ExecuteAsync_DeclaredNonExecutingFunction_ShouldNotBeRejectedAsUnknown()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-completeness-declared" });
        var database = await engine.CreateDatabaseAsync("completeness");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedAsync(session, withRows: false);

        // Act
        var rows = await RowsAsync(session, "SELECT NULLIF(id, 1) FROM t;");

        // Assert
        rows.ShouldBeEmpty();
        await ExecuteAsync(session, _rows[0]);
        (await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT NULLIF(id, 1) FROM t;")))
            .Message.ShouldNotContain("Unknown function", Case.Sensitive);
    }

    private static async Task SeedAsync(IDatabaseSession session, bool withRows)
    {
        foreach (string statement in withRows ? [.. _schema, .. _rows] : _schema)
        {
            (await ExecuteAsync(session, statement)).Status.ShouldBe(QueryResultStatus.Success, statement);
        }
    }

    private static async Task<string[]> SnapshotAsync(IDatabaseSession session)
    {
        var t = await RowsAsync(session, "SELECT id, name, age, flag FROM t ORDER BY id;");
        var u = await RowsAsync(session, "SELECT id, name FROM u ORDER BY id;");
        return [.. t.Select(row => "t:" + string.Join(",", row)), .. u.Select(row => "u:" + string.Join(",", row))];
    }

    private static Task<QueryResult> ExecuteAsync(IDatabaseSession session, string statement)
        => session.ExecuteAsync(statement, cancellationToken: CancellationToken.None).AsTask();

    private static async Task<List<object?[]>> RowsAsync(IDatabaseSession session, string statement)
    {
        await using var result = (await ExecuteAsync(session, statement)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < row.FieldCount; i++)
            {
                values[i] = row.GetValue(i);
            }
            rows.Add(values);
        }
        return rows;
    }
}
