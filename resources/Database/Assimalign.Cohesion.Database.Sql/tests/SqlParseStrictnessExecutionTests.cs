using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Measures parse strictness (#1101) against the live engine: a statement with a character
/// outside the dialect, or with infix regex <c>~</c>, never executes on either session seam, over an empty
/// table as over a populated one.
/// </summary>
public sealed class SqlParseStrictnessExecutionTests
{
    private const string schema = "CREATE TABLE t (id INT PRIMARY KEY, a INT, b INT);";
    private const string rowsValue = "INSERT INTO t VALUES (1, 10, 100), (2, 20, 200);";

    /// <summary>
    /// A stray character used to lex as a one-character identifier: SELECT * FROM t ? read
    /// ? as an alias and returned every row, and DELETE FROM t ? deleted them. Each reports
    /// CS00003 at the character and changes nothing.
    /// </summary>
    /// <param name="sql">A statement with a character outside the dialect.</param>
    /// <param name="code">The syntax or recognized unsupported-surface identity.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Strictness: a stray character or deferred symbol has a coded refusal and nothing executes")]
    [InlineData("SELECT * FROM t ?;")]
    [InlineData("SELECT * FROM t #;")]
    [InlineData("# DELETE FROM t;")]
    [InlineData("SELECT * FROM t ^;", "CK00001")]
    [InlineData("SELECT a ^ b FROM t;", "CK00001")]
    [InlineData("SELECT a FROM t WHERE b = ?;")]
    [InlineData("SELECT ? FROM t;")]
    [InlineData("DELETE FROM t ?;")]
    [InlineData("DELETE FROM t WHERE b = ?;")]
    [InlineData("UPDATE t SET a = ? WHERE id = 1;")]
    [InlineData("UPDATE t § SET a = 0;")]
    [InlineData("INSERT INTO t VALUES (3, ?, 300);")]
    // A non-ASCII decimal digit used to lex as an Integer, parse cleanly and then throw a raw
    // FormatException at execution.
    [InlineData("SELECT ٣ FROM t;")]
    [InlineData("DELETE FROM t WHERE id = ١;")]
    [InlineData("UPDATE t SET a = ٥ WHERE id = 1;")]
    [InlineData("INSERT INTO t VALUES (٣, 1, 1);")]
    // So did an exponent marker without digits.
    [InlineData("SELECT 1e FROM t;")]
    [InlineData("DELETE FROM t WHERE a = 1e+;")]
    // A stray character before the statement, such as a byte order mark.
    [InlineData("﻿DELETE FROM t;")]
    public async Task ExecuteAsync_StrayCharacter_ShouldReportSyntaxErrorWithoutExecuting(string sql, string code = "CS00003")
        => await AssertRejectedAsync(sql, code);

    /// <summary>
    /// Infix ~ remains deferred regular-expression matching and reports CK00001 even
    /// over empty inputs. Prefix bitwise NOT executes under #1721.
    /// </summary>
    /// <param name="sql">A statement using ~.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Strictness: ~ reports CK00001 and nothing executes, whatever the rows")]
    [InlineData("SELECT a FROM t WHERE a ~ 'x';")]
    [InlineData("DELETE FROM t WHERE a ~ '1';")]
    [InlineData("DELETE FROM t WHERE a IS NOT NULL ~ '1';")]
    [InlineData("DELETE FROM t WHERE a IN (10, 20) ~ '1';")]
    [InlineData("DELETE FROM t WHERE a !~ '1';")]
    [InlineData("UPDATE t SET a = 1 WHERE b ~* 'x';")]
    public async Task ExecuteAsync_Tilde_ShouldReportUnsupportedOperatorWithoutExecuting(string sql)
        => await AssertRejectedAsync(sql, "CK00001");

    private static async Task AssertRejectedAsync(string sql, string code)
    {
        foreach (bool withRows in new[] { false, true })
        {
            // Arrange
            await using var engine = SqlDatabaseEngine.Create("sql-strictness", new SqlDatabaseEngineOptions());
            var database = await engine.CreateDatabaseAsync("strictness");
            await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
            await ExecuteAsync(session, schema);
            if (withRows)
            {
                await ExecuteAsync(session, rowsValue);
            }
            var before = await SnapshotAsync(session);

            // Act
            var textError = await Should.ThrowAsync<DatabaseParseException>(() => ExecuteAsync(session, sql));
            var parsed = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
            var typed = await Should.ThrowAsync<DatabaseParseException>(() => session.ExecuteAsync(new SqlQueryRequest(parsed), CancellationToken.None).AsTask());

            // Assert
            string context = $"{sql} (withRows: {withRows})";
            textError.Message.ShouldStartWith($"SQL parse error {code}: ", Case.Sensitive, context);
            parsed.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ShouldHaveSingleItem(context).Code.ShouldBe(code, context);
            typed.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message, diagnostic.Start, diagnostic.End)).ShouldBe(textError.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message, diagnostic.Start, diagnostic.End)), context);
            session.CurrentTransaction.ShouldBeNull(context);
            (await SnapshotAsync(session)).ShouldBe(before, context);
        }
    }

    private static async Task<string[]> SnapshotAsync(SqlDatabaseSession session)
    {
        await using var result = (await ExecuteAsync(session, "SELECT id, a, b FROM t ORDER BY id;")).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(string.Join(",", Enumerable.Range(0, row.FieldCount).Select(row.GetValue)));
        }
        return [.. rows];
    }

    private static Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string statement)
        => session.ExecuteAsync(statement, cancellationToken: CancellationToken.None).AsTask();
}
