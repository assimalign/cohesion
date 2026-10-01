using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Measures parse strictness (#1101) against the live engine: a statement with a character
/// outside the dialect, or with <c>~</c>, never executes on either session seam, over an empty
/// table as over a populated one.
/// </summary>
public sealed class SqlParseStrictnessExecutionTests
{
    private const string Schema = "CREATE TABLE t (id INT PRIMARY KEY, a INT, b INT);";
    private const string Rows = "INSERT INTO t VALUES (1, 10, 100), (2, 20, 200);";

    /// <summary>
    /// A stray character used to lex as a one-character identifier: SELECT * FROM t ? read
    /// ? as an alias and returned every row, and DELETE FROM t ? deleted them. Each reports
    /// SQL0003 at the character and changes nothing.
    /// </summary>
    /// <param name="sql">A statement with a character outside the dialect.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Strictness: a stray character reports SQL0003 and nothing executes")]
    [InlineData("SELECT * FROM t ?;")]
    [InlineData("SELECT * FROM t #;")]
    [InlineData("SELECT * FROM t ^;")]
    [InlineData("SELECT a # b FROM t;")]
    [InlineData("SELECT a FROM t WHERE b = ?;")]
    [InlineData("SELECT ? FROM t;")]
    [InlineData("DELETE FROM t ?;")]
    [InlineData("DELETE FROM t WHERE b = ?;")]
    [InlineData("UPDATE t SET a = ? WHERE id = 1;")]
    [InlineData("UPDATE t § SET a = 0;")]
    [InlineData("INSERT INTO t VALUES (3, ?, 300);")]
    public async Task ExecuteAsync_StrayCharacter_ShouldReportSyntaxErrorWithoutExecuting(string sql)
        => await AssertRejectedAsync(sql, "SQL0003");

    /// <summary>
    /// ~ used to parse as bitwise NOT, whose evaluator returned NULL for a NULL operand and
    /// threw per row otherwise, so SELECT ~NULL and ~ over an empty table succeeded. Prefix and
    /// infix ~ now report COHDBL001 at parse time.
    /// </summary>
    /// <param name="sql">A statement using ~.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Strictness: ~ reports COHDBL001 and nothing executes, whatever the rows")]
    [InlineData("SELECT ~NULL;")]
    [InlineData("SELECT ~a FROM t;")]
    [InlineData("SELECT a FROM t WHERE a ~ 'x';")]
    [InlineData("SELECT a FROM t WHERE ~a = 1;")]
    [InlineData("UPDATE t SET a = ~a;")]
    [InlineData("DELETE FROM t WHERE a ~ '1';")]
    public async Task ExecuteAsync_Tilde_ShouldReportUnsupportedOperatorWithoutExecuting(string sql)
        => await AssertRejectedAsync(sql, "COHDBL001");

    private static async Task AssertRejectedAsync(string sql, string code)
    {
        foreach (bool withRows in new[] { false, true })
        {
            // Arrange
            await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-strictness" });
            var database = await engine.CreateDatabaseAsync("strictness");
            await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
            await ExecuteAsync(session, Schema);
            if (withRows)
            {
                await ExecuteAsync(session, Rows);
            }
            var before = await SnapshotAsync(session);

            // Act
            var textError = await Should.ThrowAsync<DatabaseParseException>(() => ExecuteAsync(session, sql));
            var parsed = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
            var typed = await session.ExecuteAsync(new SqlQueryRequest(parsed), CancellationToken.None);

            // Assert
            string context = $"{sql} (withRows: {withRows})";
            textError.Message.ShouldStartWith($"SQL parse error {code}: ", Case.Sensitive, context);
            parsed.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ShouldHaveSingleItem(context).Code.ShouldBe(code, context);
            typed.Status.ShouldBe(QueryResultStatus.Error, context);
            typed.ShouldNotBeAssignableTo<QueryResultSet>(context);
            session.CurrentTransaction.ShouldBeNull(context);
            (await SnapshotAsync(session)).ShouldBe(before, context);
        }
    }

    private static async Task<string[]> SnapshotAsync(IDatabaseSession session)
    {
        await using var result = (await ExecuteAsync(session, "SELECT id, a, b FROM t ORDER BY id;")).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(string.Join(",", Enumerable.Range(0, row.FieldCount).Select(row.GetValue)));
        }
        return [.. rows];
    }

    private static Task<QueryResult> ExecuteAsync(IDatabaseSession session, string statement)
        => session.ExecuteAsync(statement, cancellationToken: CancellationToken.None).AsTask();
}
