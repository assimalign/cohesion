using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlPredicateParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Predicates: PostgreSQL precedence matches explicit grouping")]
    [InlineData("a OR b AND c", "a OR (b AND c)")]
    [InlineData("a AND NOT b", "a AND (NOT b)")]
    [InlineData("NOT a IS NULL", "NOT (a IS NULL)")]
    [InlineData("a = b IS TRUE", "(a = b) IS TRUE")]
    [InlineData("a = b IS NULL", "(a = b) IS NULL")]
    [InlineData("a = b LIKE c", "a = (b LIKE c)")]
    [InlineData("a = b ILIKE c", "a = (b ILIKE c)")]
    [InlineData("NOT a ILIKE b", "NOT (a ILIKE b)")]
    [InlineData("a NOT ILIKE b || c ESCAPE d || e", "a NOT ILIKE (b || c) ESCAPE (d || e)")]
    [InlineData("-a::INT", "-(CAST(a AS INT))")]
    [InlineData("+1::TEXT", "+CAST(1 AS TEXT)")]
    [InlineData("+(1::TEXT)", "+CAST(1 AS TEXT)")]
    [InlineData("+1::REGCLASS", "+CAST(1 AS REGCLASS)")]
    [InlineData("a COLLATE binary::TEXT", "CAST(a COLLATE binary AS TEXT)")]
    [InlineData("a IN (1)::INT", "CAST(a IN (1) AS INT)")]
    [InlineData("a + b::INT * c", "a + (CAST(b AS INT) * c)")]
    [InlineData("a::TEXT::INT", "CAST(CAST(a AS TEXT) AS INT)")]
    [InlineData("a IN (1) = TRUE", "(a IN (1)) = TRUE")]
    [InlineData("1 IN (1) IN (TRUE)", "(1 IN (1)) IN (TRUE)")]
    [InlineData("1 NOT IN (2) BETWEEN FALSE AND TRUE", "(1 NOT IN (2)) BETWEEN FALSE AND TRUE")]
    [InlineData("1 IN (SELECT 1) IN (TRUE)", "(1 IN (SELECT 1)) IN (TRUE)")]
    [InlineData("1 NOT IN (SELECT 2) BETWEEN FALSE AND TRUE", "(1 NOT IN (SELECT 2)) BETWEEN FALSE AND TRUE")]
    [InlineData("1 IN (SELECT 1) NOT IN (SELECT FALSE)", "(1 IN (SELECT 1)) NOT IN (SELECT FALSE)")]
    [InlineData("1 NOT IN (SELECT 2) IN (TRUE) IN (SELECT TRUE)", "((1 NOT IN (SELECT 2)) IN (TRUE)) IN (SELECT TRUE)")]
    [InlineData("1 IN (SELECT 1) = TRUE", "(1 IN (SELECT 1)) = TRUE")]
    [InlineData("NOT EXISTS (SELECT 1) IS TRUE", "NOT ((EXISTS (SELECT 1)) IS TRUE)")]
    [InlineData("NOT EXISTS (SELECT 1) = FALSE", "NOT ((EXISTS (SELECT 1)) = FALSE)")]
    [InlineData("a IS DISTINCT FROM b = c", "a IS DISTINCT FROM (b = c)")]
    [InlineData("a IS NULL || b", "(a IS NULL) || b")]
    [InlineData("a IS NULL COLLATE binary", "(a IS NULL) COLLATE binary")]
    [InlineData("a = NOT b", "a = (NOT b)")]
    [InlineData("a IS NULL = b", "(a IS NULL) = b")]
    [InlineData("a LIKE b || c ESCAPE d || e", "a LIKE (b || c) ESCAPE (d || e)")]
    [InlineData("a BETWEEN b = c AND d", "a BETWEEN (b = c) AND d")]
    [InlineData("a || b + c", "a || (b + c)")]
    [InlineData("a + b * c", "a + (b * c)")]
    [InlineData("a * -b", "a * (-b)")]
    [InlineData("-a COLLATE binary", "(-a) COLLATE binary")]
    public void Parse_PrecedenceBoundary_ShouldMatchPostgreSqlTree(string text, string grouped)
    {
        // Arrange
        var expected = Parse(grouped);
        // Act
        var actual = Parse(text);
        // Assert
        SqlTreeDump.Dump(actual).ShouldBe(SqlTreeDump.Dump(expected));
        SqlTreeDump.Dump(Parse(SqlExpressionRenderer.Render(actual))).ShouldBe(SqlTreeDump.Dump(actual));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Predicates: Boolean tests have dedicated truth values")]
    [InlineData("TRUE", SqlTruthValue.True)]
    [InlineData("FALSE", SqlTruthValue.False)]
    [InlineData("UNKNOWN", SqlTruthValue.Unknown)]
    public void Parse_BooleanTest_ShouldRetainTruthAndNegation(string word, SqlTruthValue truth)
    {
        // Arrange / Act
        var positive = Parse("flag IS " + word).ShouldBeOfType<SqlBooleanTestExpression>();
        var negative = Parse("flag IS NOT " + word).ShouldBeOfType<SqlBooleanTestExpression>();
        // Assert
        positive.TruthValue.ShouldBe(truth);
        positive.IsNegated.ShouldBeFalse();
        negative.TruthValue.ShouldBe(truth);
        negative.IsNegated.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Predicates: distinct and ESCAPE operands round trip")]
    [InlineData("a IS DISTINCT FROM b")]
    [InlineData("a IS NOT DISTINCT FROM @b")]
    [InlineData("name LIKE 'a!%' ESCAPE '!'")]
    [InlineData("name NOT LIKE @p || '%' ESCAPE @e || ''")]
    [InlineData("name LIKE pattern ESCAPE escape_column")]
    [InlineData("name ILIKE pattern ESCAPE escape_column")]
    [InlineData("name NOT ILIKE @p ESCAPE @e")]
    [InlineData("name LIKE 'a%' ESCAPE NULL")]
    public void Parse_PredicateOperands_ShouldRoundTrip(string text)
    {
        // Arrange / Act
        var expression = Parse(text);
        string rendered = SqlExpressionRenderer.Render(expression);
        // Assert
        SqlTreeDump.Dump(Parse(rendered)).ShouldBe(SqlTreeDump.Dump(expression));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Predicates: incomplete forms report one syntax error at the missing token")]
    [InlineData("a IS DISTINCT b", "b")]
    [InlineData("a IS NOT banana", "banana")]
    [InlineData("a IS DISTINCT FROM;", ";")]
    [InlineData("a LIKE 'x' ESCAPE;", ";")]
    [InlineData("a ILIKE 'x' ESCAPE;", ";")]
    [InlineData("a::;", ";")]
    public void Parse_IncompletePredicate_ShouldReportSyntaxAtOperand(string text, string token)
    {
        // Arrange
        string sql = "SELECT " + text + (text.EndsWith(';') ? "" : ";");
        // Act
        var statement = new SqlQueryParser().Parse(sql);
        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("CS00003");
        error.Start.ShouldBe(sql.LastIndexOf(token, System.StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Predicates: excluded operators have one named capability error")]
    [InlineData("a ISNULL", "ISNULL")]
    [InlineData("a notnull", "NOTNULL")]
    [InlineData("a notnull FROM t", "NOTNULL")]
    [InlineData("a notnull + 1 FROM t", "NOTNULL")]
    [InlineData("a SIMILAR TO p", "SIMILAR TO")]
    [InlineData("a NOT SIMILAR TO p", "NOT SIMILAR TO")]
    [InlineData("a & b", "&")]
    [InlineData("a # b", "#")]
    [InlineData("a ^ b", "^")]
    [InlineData("a | b", "|")]
    [InlineData("a << b", "<<")]
    [InlineData("a >> b", ">>")]
    [InlineData("a IS JSON", "IS JSON")]
    [InlineData("a IS OF (INT)", "IS OF")]
    [InlineData("a IS NORMALIZED", "IS NORMALIZED")]
    public void Parse_ExcludedOperator_ShouldReportSingleNamedCapability(string text, string form)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("SELECT " + text + ";");
        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("CK00001");
        error.Message.ShouldNotBeNull().ShouldContain(form, Case.Sensitive);
        error.Start.ShouldBe(text.Contains(' ') ? 9 : 8);
        error.End.ShouldBe(error.Start + (text.Contains("::", System.StringComparison.Ordinal) ? 2 : form.Length));
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Predicates: adjacent less-than and minus form a comparison (#1647)")]
    public void Parse_AdjacentLeftArrow_ShouldCompareWithNegativeLiteral()
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("SELECT a<-1;");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var expression = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.ShouldHaveSingleItem().Expression;
        SqlExpressionRenderer.Render(expression).ShouldBe("a < -1");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Predicates: contextual operator words remain ordinary identifiers")]
    public void Parse_ContextualIdentifiers_ShouldKeepCanonicalSpelling()
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("SELECT ilike, similar, escape, unknown FROM t;");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
        select.Columns.Select(c => SqlExpressionRenderer.Render(c.Expression)).ShouldBe(["ilike", "similar", "\"escape\"", "\"unknown\""]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Predicates: loop-built truth tests respect the expression depth limit")]
    [InlineData(" IS TRUE")]
    [InlineData(" IS NULL")]
    [InlineData("::INT")]
    [InlineData(" IS NOT UNKNOWN")]
    public void Parse_ChainedTests_ShouldApplyNestingLimit(string suffix)
    {
        // Arrange
        var parser = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = 32 });
        string text = "SELECT NULL" + string.Concat(Enumerable.Repeat(suffix, 32)) + ";";
        // Act
        var statement = parser.Parse(text);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS00006");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Predicates: nonassociative rungs refuse the second operator once")]
    [InlineData("a < b < c", "<")]
    [InlineData("a = b = c", "=")]
    [InlineData("a IS DISTINCT FROM b IS NULL", "IS")]
    [InlineData("a LIKE b LIKE c", "LIKE")]
    [InlineData("a ILIKE b ILIKE c", "ILIKE")]
    [InlineData("a BETWEEN 1 AND 2 LIKE c", "LIKE")]
    public void Parse_NonassociativeChain_ShouldLocateSecondOperator(string text, string second)
    {
        // Arrange
        string sql = "SELECT " + text + " FROM t;";
        // Act
        var statement = new SqlQueryParser().Parse(sql);
        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("CS00003");
        error.Start.ShouldBe(sql.LastIndexOf(second, System.StringComparison.Ordinal));
        error.End.ShouldBe(error.Start + second.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Predicates: restricted DEFAULT boundary round trips")]
    [InlineData("1 = 1")]
    [InlineData("1 IS DISTINCT FROM 2 NOT NULL")]
    public void Parse_DefaultBoundary_ShouldRetainConstraintAndRoundTrip(string definition)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("CREATE TABLE t (flag BOOLEAN DEFAULT " + definition + ");");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var column = statement.SqlExpression.ShouldBeOfType<SqlCreateTableExpression>().Columns.Single();
        string rendered = SqlExpressionRenderer.Render(column.DefaultValue!);
        SqlTreeDump.Dump(Parse(rendered)).ShouldBe(SqlTreeDump.Dump(column.DefaultValue!));
        column.IsNullable.ShouldBe(!definition.EndsWith("NOT NULL", System.StringComparison.Ordinal));
        rendered.ShouldBe(definition.Replace(" NOT NULL", ""));
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Predicates: NOT NULL hint directs callers to IS NOT NULL")]
    public void Parse_NotNullAfterOperand_ShouldRetainHint()
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("SELECT a NOT NULL FROM t;");
        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("CS00003");
        error.Message.ShouldNotBeNull().ShouldContain("write IS NOT NULL", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Predicates: compact NOT EXISTS retains the EXISTS nesting budget")]
    public void Parse_NotExistsAtLimit_ShouldPreserveCompactDepth()
    {
        // Arrange
        var parser = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = 32 });
        string query = "EXISTS (SELECT " + string.Concat(Enumerable.Repeat("- ", 30)) + "1)";
        // Act
        var positive = parser.Parse("SELECT " + query + ";").ShouldBeOfType<SqlQueryStatement>();
        var negative = parser.Parse("SELECT NOT " + query + ";").ShouldBeOfType<SqlQueryStatement>();
        // Assert
        positive.Diagnostics.ShouldBeEmpty();
        negative.Diagnostics.ShouldBeEmpty();
        negative.ExpressionNestingDepth.ShouldBe(positive.ExpressionNestingDepth);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Postfix CAST: all CAST target grammar is shared (#1721)")]
    [InlineData("INT")]
    [InlineData("INTEGER")]
    [InlineData("TEXT")]
    [InlineData("VARCHAR(10)")]
    [InlineData("DECIMAL(10,2)")]
    [InlineData("DOUBLE PRECISION")]
    [InlineData("NATIONAL CHARACTER VARYING(10)")]
    [InlineData("TIMESTAMP WITH TIME ZONE")]
    [InlineData("TIME WITHOUT TIME ZONE")]
    [InlineData("BINARY VARYING(10)")]
    [InlineData("my_domain")]
    [InlineData("\"a domain\"")]
    [InlineData("REGCLASS")]
    public void Parse_PostfixTarget_ShouldMatchCast(string target)
    {
        // Arrange / Act
        var suffix = Parse("value::" + target);
        var cast = Parse("CAST(value AS " + target + ")");
        // Assert
        SqlTreeDump.Dump(suffix).ShouldBe(SqlTreeDump.Dump(cast));
        SqlExpressionRenderer.Render(suffix).ShouldBe(SqlExpressionRenderer.Render(cast));
    }

    private static SqlExpression Parse(string text)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("SELECT " + text + ";");
        statement.Diagnostics.ShouldBeEmpty(text);
        return statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression;
    }
}
