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
    [InlineData("«?» SELECT * FROM t;")]
    [InlineData("«﻿»SELECT a FROM t;")]
    [InlineData("SELECT «٣» FROM t;")]
    [InlineData("SELECT a FROM t WHERE a = «１»;")]
    [InlineData("DELETE FROM t WHERE id = «١»;")]
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

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Strictness: text of stray characters only is one SQL0003 and no unknown command")]
    public void Parse_OnlyStrayCharacters_ShouldReportOnlyTheCharacter()
    {
        // Act
        var statement = Parse("?;");

        // Assert
        Errors(statement).ShouldHaveSingleItem().Code.ShouldBe("SQL0003");
        statement.SqlExpression.CommandType.ShouldBe(SqlQueryCommandType.Unknown);
    }

    /// <summary>
    /// An exponent marker without digits used to parse cleanly and then fail at execution with
    /// a raw FormatException naming no code or span.
    /// </summary>
    /// <param name="marked">The statement; the literal sits between '«' and '»'.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Strictness: a numeric literal whose exponent has no digits is one SQL0003")]
    [InlineData("SELECT «1e» FROM t;")]
    [InlineData("SELECT «1E+» FROM t;")]
    [InlineData("SELECT a FROM t WHERE a = «2.5e-»;")]
    public void Parse_ExponentWithoutDigits_ShouldReportOneSyntaxError(string marked)
    {
        // Arrange
        var (sql, start, end) = Unmark(marked);

        // Act
        var error = Errors(Parse(sql)).ShouldHaveSingleItem();

        // Assert
        error.Code.ShouldBe("SQL0003");
        error.Start.ShouldBe(start);
        error.End.ShouldBe(end);
        error.Message.ShouldBe($"Malformed numeric literal '{sql[start..end]}': the exponent has no digits.");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Strictness: a numeric literal with exponent digits still parses")]
    public void Parse_ExponentWithDigits_ShouldParse()
        => Errors(Parse("SELECT 1e5, 2.5E-3, .5e+2 FROM t;")).ShouldBeEmpty();

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
    // Rules that take a primary directly, and a run of prefix operators
    [InlineData("SELECT a FROM t WHERE s LIKE «~»'x';", "prefix")]
    [InlineData("CREATE TABLE u (a INT DEFAULT «~»1);", "prefix")]
    [InlineData("SELECT a FROM t WHERE «~ ~»a = 1;", "prefix")]
    // After a predicate that completes without returning to the additive loop
    [InlineData("SELECT a FROM t WHERE s LIKE 'x' «~» 'y';", "infix")]
    [InlineData("SELECT a FROM t WHERE a IS NULL «~» 'y';", "infix")]
    [InlineData("SELECT a FROM t WHERE a IN (1, 2) «~» 'x';", "infix")]
    [InlineData("SELECT a FROM t WHERE a BETWEEN 1 AND 2 «~» 3;", "infix")]
    [InlineData("SELECT a FROM t WHERE a IS NOT NULL «~» 'y' AND b = 1;", "infix")]
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

    /// <summary>
    /// PostgreSQL's pattern-match operators built on <c>~</c> are one operator each. They used to
    /// report two diagnostics, or a generic SQL0003 at the <c>!</c>.
    /// </summary>
    /// <param name="marked">The statement; the operator sits between '«' and '»'.</param>
    /// <param name="meaning">The match the message names.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Strictness: compound ~ operators report one COHDBL001 naming the whole operator")]
    [InlineData("SELECT a FROM t WHERE a «~*» 'x';", "regular-expression match")]
    [InlineData("SELECT a FROM t WHERE a «!~» 'x';", "regular-expression match")]
    [InlineData("SELECT a FROM t WHERE a «!~*» 'x';", "regular-expression match")]
    [InlineData("SELECT a FROM t WHERE a «~~» 'x';", "LIKE match")]
    [InlineData("SELECT a FROM t WHERE a «~~*» 'x';", "LIKE match")]
    [InlineData("SELECT a FROM t WHERE a «!~~» 'x';", "LIKE match")]
    [InlineData("SELECT a FROM t WHERE a «!~~*» 'x';", "LIKE match")]
    [InlineData("SELECT a FROM t WHERE a IS NULL «!~» 'x';", "regular-expression match")]
    public void Parse_CompoundTilde_ShouldReportOneUnsupportedOperator(string marked, string meaning)
    {
        // Arrange
        var (sql, start, end) = Unmark(marked);

        // Act
        var error = Errors(Parse(sql)).ShouldHaveSingleItem();

        // Assert
        error.Code.ShouldBe("COHDBL001");
        error.Start.ShouldBe(start);
        error.End.ShouldBe(end);
        error.Message.ShouldBe($"The infix {sql[start..end]} operator ({meaning}) is not supported by the SQL surface.");
    }

    /// <summary>
    /// Each used to report its COHDBL001 plus a second diagnostic from the text after it: a
    /// quantified operand parsed as a call to a function named ANY, LATERAL bound as a table
    /// name, and WITHIN read as a column alias.
    /// </summary>
    /// <param name="marked">The statement; the reported span sits between '«' and '»'.</param>
    /// <param name="construct">The construct the message names.</param>
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Strictness: a recognized construct is one COHDBL001 and its text is skipped")]
    [InlineData("SELECT a FROM t WHERE a = «ANY» (SELECT b FROM u);", "ANY quantified comparison")]
    [InlineData("SELECT a FROM t WHERE a <> «SOME» (SELECT b FROM u) AND a > 1;", "SOME quantified comparison")]
    [InlineData("SELECT a FROM t WHERE a >= «ALL» (SELECT b FROM u);", "ALL quantified comparison")]
    [InlineData("SELECT * FROM t JOIN «LATERAL» (SELECT 1) x ON TRUE;", "LATERAL subquery")]
    [InlineData("SELECT * FROM «LATERAL» (SELECT b FROM u) AS x;", "LATERAL subquery")]
    [InlineData("SELECT PERCENTILE_CONT(0.5) «WITHIN GROUP» (ORDER BY a) FROM t;", "WITHIN GROUP")]
    [InlineData("SELECT MODE() «WITHIN GROUP» (ORDER BY a) AS m FROM t;", "WITHIN GROUP")]
    [InlineData("SELECT COUNT(*) «FILTER» (WHERE a > 1) AS n FROM t;", "FILTER")]
    [InlineData("«DROP VIEW» v;", "DROP VIEW")]
    [InlineData("«CREATE VIEW» v AS SELECT a FROM t;", "CREATE VIEW")]
    public void Parse_RecognizedConstruct_ShouldReportOneDiagnostic(string marked, string construct)
    {
        // Arrange
        var (sql, start, end) = Unmark(marked);

        // Act
        var error = Errors(Parse(sql)).ShouldHaveSingleItem();

        // Assert
        error.Code.ShouldBe("COHDBL001");
        error.Start.ShouldBe(start);
        error.End.ShouldBe(end);
        error.Message.ShouldBe($"The {construct} clause is not supported by the SQL surface of this database model.");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Strictness: a skipped construct after the scan's clause is reported once by the parser")]
    public void Parse_SecondSkippedConstruct_ShouldReportEachOnce()
    {
        // Arrange: the preflight scan reports only the first clause, ANY.
        const string sql = "SELECT a FROM t WHERE a = ANY (SELECT b FROM u) AND b = ALL (SELECT c FROM v);";

        // Act
        var errors = Errors(Parse(sql));

        // Assert
        errors.Select(error => (error.Code, error.Message)).ShouldBe(
        [
            ("COHDBL001", "The ALL quantified comparison clause is not supported by the SQL surface of this database model."),
            ("COHDBL001", "The ANY quantified comparison clause is not supported by the SQL surface of this database model."),
        ], ignoreOrder: true);
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
