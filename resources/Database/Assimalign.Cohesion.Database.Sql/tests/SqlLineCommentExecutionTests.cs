using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Measures the line-comment boundary (#1150) against the live engine. A <c>--</c> comment
/// used to end only at LF, so <c>DELETE FROM t -- c&lt;CR&gt;WHERE id = 1;</c> parsed as an
/// unfiltered DELETE and removed every row. On both session seams, and at every line
/// terminator, the WHERE now stays in effect and exactly one row changes.
/// </summary>
public sealed class SqlLineCommentExecutionTests
{
    private const string Schema = "CREATE TABLE t (id INT PRIMARY KEY, a INT);";
    private const string Rows = "INSERT INTO t VALUES (1, 10), (2, 20), (3, 30);";

    /// <param name="terminator">The line terminator, by name (see <see cref="Terminator"/>).</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Comments: DELETE with a WHERE after a line comment deletes exactly one row")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public async Task ExecuteAsync_DeleteWithWhereAfterLineComment_ShouldDeleteExactlyOneRow(string terminator)
    {
        // Arrange
        string sql = "DELETE FROM t -- c" + Terminator(terminator) + "WHERE id = 1;";

        foreach (bool typed in new[] { false, true })
        {
            await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-line-comment-delete" });
            await using var session = await SeedAsync(engine);

            // Act
            var result = await RunAsync(session, sql, typed);

            // Assert
            string context = $"{terminator} (typed: {typed})";
            result.Status.ShouldBe(QueryResultStatus.Success, context);
            result.AffectedCount.ShouldBe(1, context);
            (await SnapshotAsync(session)).ShouldBe(["2,20", "3,30"], context);
        }
    }

    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Comments: UPDATE with a WHERE after a line comment updates exactly one row")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public async Task ExecuteAsync_UpdateWithWhereAfterLineComment_ShouldUpdateExactlyOneRow(string terminator)
    {
        // Arrange
        string sql = "UPDATE t SET a = 1 -- c" + Terminator(terminator) + "WHERE id = 1;";

        foreach (bool typed in new[] { false, true })
        {
            await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-line-comment-update" });
            await using var session = await SeedAsync(engine);

            // Act
            var result = await RunAsync(session, sql, typed);

            // Assert
            string context = $"{terminator} (typed: {typed})";
            result.Status.ShouldBe(QueryResultStatus.Success, context);
            result.AffectedCount.ShouldBe(1, context);
            (await SnapshotAsync(session)).ShouldBe(["1,1", "2,20", "3,30"], context);
        }
    }

    /// <summary>
    /// A second statement on the line after a comment is leftover text: it fails at parse time
    /// and nothing executes, where it used to vanish into the comment.
    /// </summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Comments: a statement after a line comment is leftover text and nothing executes")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public async Task ExecuteAsync_StatementAfterLineComment_ShouldFailWithoutExecuting(string terminator)
    {
        // Arrange
        string sql = "DELETE FROM t WHERE id = 1 -- c" + Terminator(terminator) + "DELETE FROM t;";
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = "sql-line-comment-leftover" });
        await using var session = await SeedAsync(engine);

        // Act
        var error = await Should.ThrowAsync<DatabaseParseException>(() => RunAsync(session, sql, typed: false));
        var typed = await RunAsync(session, sql, typed: true);

        // Assert
        error.Message.ShouldStartWith("SQL parse error SQL0003: ", Case.Sensitive);
        typed.Status.ShouldBe(QueryResultStatus.Error);
        (await SnapshotAsync(session)).ShouldBe(["1,10", "2,20", "3,30"]);
    }

    private static async Task<IDatabaseSession> SeedAsync(SqlDatabaseEngine engine)
    {
        var database = await engine.CreateDatabaseAsync("comments");
        var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        (await RunAsync(session, Schema, typed: false)).Status.ShouldBe(QueryResultStatus.Success);
        (await RunAsync(session, Rows, typed: false)).AffectedCount.ShouldBe(3);
        return session;
    }

    /// <summary>Runs <paramref name="sql"/> through the text seam, or parsed through the typed-request seam.</summary>
    private static Task<QueryResult> RunAsync(IDatabaseSession session, string sql, bool typed)
        => typed
            ? session.ExecuteAsync(new SqlQueryRequest((SqlQueryStatement)new SqlQueryParser().Parse(sql)), CancellationToken.None).AsTask()
            : session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask();

    private static async Task<string[]> SnapshotAsync(IDatabaseSession session)
    {
        await using var result = (await RunAsync(session, "SELECT id, a FROM t ORDER BY id;", typed: false)).ShouldBeAssignableTo<QueryResultSet>();
        var rows = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(string.Join(",", Enumerable.Range(0, row.FieldCount).Select(row.GetValue)));
        }
        return [.. rows];
    }

    /// <summary>The line terminators by name, so no invisible character sits in the test source.</summary>
    /// <param name="name">LF, CR, CRLF, NEL, LS or PS.</param>
    /// <returns>The terminator text.</returns>
    private static string Terminator(string name) => name switch
    {
        "LF" => "\n",
        "CR" => "\r",
        "CRLF" => "\r\n",
        "NEL" => ((char)0x0085).ToString(),
        "LS" => ((char)0x2028).ToString(),
        "PS" => ((char)0x2029).ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown line terminator."),
    };
}
