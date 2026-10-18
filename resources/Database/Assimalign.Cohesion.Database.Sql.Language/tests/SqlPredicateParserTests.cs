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
    [InlineData("a IN (1) = TRUE", "(a IN (1)) = TRUE")]
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
    [InlineData("-a COLLATE binary", "-(a COLLATE binary)")]
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
    public void Parse_IncompletePredicate_ShouldReportSyntaxAtOperand(string text, string token)
    {
        // Arrange
        string sql = "SELECT " + text + (text.EndsWith(';') ? "" : ";");
        // Act
        var statement = new SqlQueryParser().Parse(sql);
        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("SQL0003");
        error.Start.ShouldBe(sql.LastIndexOf(token, System.StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Predicates: excluded operators have one named capability error")]
    [InlineData("a ILIKE p", "ILIKE")]
    [InlineData("a NOT ILIKE p", "NOT ILIKE")]
    [InlineData("a SIMILAR TO p", "SIMILAR TO")]
    [InlineData("a NOT SIMILAR TO p", "NOT SIMILAR TO")]
    [InlineData("a & b", "&")]
    [InlineData("a | b", "|")]
    [InlineData("a << b", "<<")]
    [InlineData("a >> b", ">>")]
    [InlineData("a::INT", "CAST(x AS type)")]
    [InlineData("a IS JSON", "IS JSON")]
    [InlineData("a IS OF (INT)", "IS OF")]
    [InlineData("a IS NORMALIZED", "IS NORMALIZED")]
    public void Parse_ExcludedOperator_ShouldReportSingleNamedCapability(string text, string form)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("SELECT " + text + ";");
        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("COHDBL001");
        error.Message.ShouldNotBeNull().ShouldContain(form, Case.Sensitive);
        error.Start.ShouldBe(text.Contains(' ') ? 9 : 8);
        error.End.ShouldBe(error.Start + (text.Contains("::", System.StringComparison.Ordinal) ? 2 : form.Length));
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Predicates: adjacent left-arrow keeps its syntax rejection")]
    public void Parse_AdjacentLeftArrow_ShouldRetainSyntaxError()
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("SELECT a<-1;");
        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("SQL0003");
        error.Start.ShouldBe(8);
        error.End.ShouldBe(10);
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
    [InlineData(" IS NOT UNKNOWN")]
    public void Parse_ChainedTests_ShouldApplyNestingLimit(string suffix)
    {
        // Arrange
        var parser = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = 32 });
        string text = "SELECT NULL" + string.Concat(Enumerable.Repeat(suffix, 32)) + ";";
        // Act
        var statement = parser.Parse(text);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("SQL0006");
    }

    private static SqlExpression Parse(string text)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("SELECT " + text + ";");
        statement.Diagnostics.ShouldBeEmpty(text);
        return statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns.Single().Expression;
    }
}
