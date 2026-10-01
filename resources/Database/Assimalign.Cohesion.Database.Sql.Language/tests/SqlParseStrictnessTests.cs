using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Parse strictness (#1101): a character outside the dialect is a syntax error at its own
/// span that binds nothing, and <c>~</c> is a recognized operator the surface rejects.
/// </summary>
public sealed class SqlParseStrictnessTests
{
    /// <summary>
    /// A stray character used to lex as a one-character identifier, so <c>SELECT * FROM t ?</c>
    /// bound <c>?</c> as an alias of <c>t</c> and ran. It is now one SQL0003 at the character,
    /// and the statement keeps only its command type: no alias, column or placeholder.
    /// </summary>
    /// <param name="marked">The statement; the stray character sits between '«' and '»'.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Strictness: a stray character is one SQL0003 at its span and binds nothing")]
    [InlineData("SELECT * FROM t «?»")]
    [InlineData("SELECT * FROM t «#»")]
    [InlineData("SELECT * FROM t «^»")]
    [InlineData("SELECT * FROM t «§»;")]
    [InlineData("SELECT a «#» b FROM t")]
    [InlineData("SELECT a FROM t WHERE b = «?»")]
    [InlineData("SELECT «?» FROM t;")]
    [InlineData("SELECT a «?» FROM t;")]
    [InlineData("DELETE FROM t «?»;")]
    [InlineData("UPDATE t SET a = «?» WHERE id = 1;")]
    [InlineData("INSERT INTO t VALUES («?»);")]
    [InlineData("SELECT * FROM t «\U0001F643»;")]
    public void Parse_StrayCharacter_ShouldReportOneSyntaxErrorAndBindNothing(string marked)
    {
        // Arrange
        var (sql, start, end) = Unmark(marked);

        // Act
        var statement = Parse(sql);

        // Assert
        var error = Errors(statement).ShouldHaveSingleItem();
        error.Code.ShouldBe("SQL0003");
        error.Start.ShouldBe(start);
        error.End.ShouldBe(end);
        error.Message!.ShouldStartWith("Unexpected character ", Case.Sensitive);
        error.Message.ShouldEndWith("; it is not part of the SQL dialect.", Case.Sensitive);
        statement.SqlExpression.GetType().ShouldBe(typeof(SqlQueryExpression));
        statement.SqlExpression.CommandType.ShouldNotBe(SqlQueryCommandType.Unknown);
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Code == "SQL0002");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Strictness: a parameter-shaped ? is never a column or a NULL literal")]
    public void Parse_QuestionMarkInExpression_ShouldProduceNoColumnOrNullLiteral()
    {
        // Act
        var where = Parse("SELECT a FROM t WHERE b = ?");
        var projection = Parse("SELECT ? FROM t");

        // Assert: the parse used to yield WHERE b = NULL and a column named ?.
        foreach (var statement in new[] { where, projection })
        {
            statement.SqlExpression.ShouldNotBeAssignableTo<SqlSelectExpression>();
            statement.SqlExpression.CommandType.ShouldBe(SqlQueryCommandType.Select);
            Errors(statement).ShouldHaveSingleItem().Message.ShouldBe("Unexpected character '?'; it is not part of the SQL dialect.");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Strictness: an invisible or supplementary stray character is named by code point")]
    [InlineData("SELECT 1 ​;", "U+200B")]
    [InlineData("SELECT 1 \u0007;", "U+0007")]
    [InlineData("SELECT 1 \U0001F643;", "U+1F643")]
    [InlineData("SELECT 1 `;", "'`'")]
    public void Parse_StrayCharacter_ShouldNameTheCharacter(string sql, string character)
    {
        // Act
        var error = Errors(Parse(sql)).ShouldHaveSingleItem();

        // Assert
        error.Message.ShouldBe($"Unexpected character {character}; it is not part of the SQL dialect.");
    }

    /// <summary>
    /// <c>~</c> is recognized but outside the executable subset. Prefix (bitwise NOT) and infix
    /// (PostgreSQL regular-expression match) each report one COHDBL001 at the operator.
    /// </summary>
    /// <param name="marked">The statement; the operator sits between '«' and '»'.</param>
    /// <param name="form">The operator form the message names.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Strictness: ~ reports one COHDBL001 naming the operator")]
    [InlineData("SELECT «~»a FROM t;", "prefix")]
    [InlineData("SELECT «~»NULL;", "prefix")]
    [InlineData("SELECT «~» -a FROM t;", "prefix")]
    [InlineData("SELECT a FROM t WHERE «~»a = 1;", "prefix")]
    [InlineData("UPDATE t SET a = «~»b WHERE id = 1;", "prefix")]
    [InlineData("SELECT a FROM t WHERE a «~» 'x';", "infix")]
    [InlineData("SELECT a «~» 'x' FROM t;", "infix")]
    [InlineData("SELECT a FROM t WHERE a = b «~» 'x';", "infix")]
    [InlineData("DELETE FROM t WHERE a «~» 'x' || 'y';", "infix")]
    [InlineData("SELECT a FROM t WHERE a «~»;", "infix")]
    public void Parse_Tilde_ShouldReportOneUnsupportedOperator(string marked, string form)
    {
        // Arrange
        var (sql, start, end) = Unmark(marked);

        // Act
        var error = Errors(Parse(sql)).ShouldHaveSingleItem();

        // Assert
        error.Code.ShouldBe("COHDBL001");
        error.Start.ShouldBe(start);
        error.End.ShouldBe(end);
        error.Message.ShouldBe(form == "prefix"
            ? "The prefix ~ operator (bitwise NOT) is not supported by the SQL surface."
            : "The infix ~ operator (regular-expression match) is not supported by the SQL surface.");
    }

    private static (string Sql, int Start, int End) Unmark(string marked)
    {
        int start = marked.IndexOf('«', StringComparison.Ordinal);
        int end = marked.IndexOf('»', StringComparison.Ordinal) - 1;
        return (marked.Replace("«", string.Empty, StringComparison.Ordinal).Replace("»", string.Empty, StringComparison.Ordinal), start, end);
    }

    private static Diagnostic[] Errors(SqlQueryStatement statement)
        => statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();

    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);
}
