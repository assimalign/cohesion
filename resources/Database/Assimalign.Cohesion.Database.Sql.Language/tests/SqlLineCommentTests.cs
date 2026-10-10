using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// A <c>--</c> comment ends at the first line terminator: LF, CR, NEL, LS or PS. The shared
/// lexer used to end it only at LF, so in <c>DELETE FROM t -- c&lt;CR&gt;WHERE id = 1</c> the
/// WHERE was comment text and the statement that parsed was an unfiltered DELETE (#1150). The
/// trailing-token rule (#1068) could not see it, because the swallowed text was part of the
/// comment token rather than left over after the statement.
/// </summary>
public sealed class SqlLineCommentTests
{
    /// <summary>The WHERE after the comment filters the DELETE and the UPDATE.</summary>
    /// <param name="terminator">The line terminator, by name (see <see cref="Terminator"/>).</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Comments: a WHERE after a line comment stays in effect at every terminator")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_WhereAfterLineComment_ShouldKeepThePredicate(string terminator)
    {
        // Arrange
        string separator = Terminator(terminator);

        // Act
        var delete = Parse("DELETE FROM t -- c" + separator + "WHERE id = 1;");
        var update = Parse("UPDATE t SET a = 1 -- c" + separator + "WHERE id = 1;");
        var select = Parse("SELECT a -- c" + separator + "FROM t -- d" + separator + "WHERE id = 1;");

        // Assert
        Errors(delete).ShouldBeEmpty();
        delete.SqlExpression.ShouldBeOfType<SqlDeleteExpression>().Where.ShouldBeOfType<SqlBinaryExpression>()
            .Operator.ShouldBe(SqlBinaryOperator.Equal);
        Errors(update).ShouldBeEmpty();
        var assignment = update.SqlExpression.ShouldBeOfType<SqlUpdateExpression>();
        assignment.Assignments.ShouldHaveSingleItem();
        assignment.Where.ShouldBeOfType<SqlBinaryExpression>().Operator.ShouldBe(SqlBinaryOperator.Equal);
        Errors(select).ShouldBeEmpty();
        var query = select.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
        query.From.ShouldNotBeNull().TableName.ShouldBe("t");
        query.Where.ShouldNotBeNull();
    }

    /// <summary>
    /// Text after the comment that does not continue the statement is leftover text, so it
    /// reports SQL0003 where it starts instead of disappearing into the comment.
    /// </summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Comments: leftover text after a line comment reports SQL0003 at every terminator")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_LeftoverAfterLineComment_ShouldReportSyntaxError(string terminator)
    {
        // Arrange
        string sql = "DELETE FROM t WHERE id = 1 -- c" + Terminator(terminator) + "DELETE FROM t;";

        // Act
        var errors = Errors(Parse(sql));

        // Assert
        var error = errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("SQL0003");
        error.Start.ShouldBe(sql.LastIndexOf("DELETE", StringComparison.Ordinal));
    }

    /// <summary>A CHECK predicate continued after a line comment keeps both halves.</summary>
    /// <param name="terminator">The line terminator, by name.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Comments: a CHECK continued after a line comment keeps both halves")]
    [InlineData("LF")]
    [InlineData("CR")]
    [InlineData("CRLF")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void Parse_CheckContinuedAfterLineComment_ShouldKeepBothHalves(string terminator)
    {
        // Arrange
        string sql = "CREATE TABLE c (a INT CHECK (a > 0 -- lower bound" + Terminator(terminator) + "AND a < 10));";

        // Act
        var statement = Parse(sql);

        // Assert
        Errors(statement).ShouldBeEmpty();
        var check = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>()
            .Constraints.ShouldHaveSingleItem().CheckExpression.ShouldNotBeNull();
        SqlExpressionRenderer.Render(check).ShouldBe("a > 0 AND a < 10");
    }

    /// <summary>
    /// <c>//</c> is not a comment in the dialect: <c>/</c> is division, so the text after it is
    /// neither hidden nor executed.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Comments: // is not a comment and reports SQL0003")]
    public void Parse_DoubleSolidus_ShouldReportSyntaxError()
    {
        // Act
        var errors = Errors(Parse("DELETE FROM t WHERE id = 1 // note\rOR id = 2;"));

        // Assert
        errors.ShouldNotBeEmpty();
        errors.ShouldAllBe(error => error.Code == "SQL0003");
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

    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);

    private static Diagnostic[] Errors(SqlQueryStatement statement)
        => [.. statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];
}
