using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Sql.Language.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlQuantifiedComparisonParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Quantified comparisons: every operator and quantifier round trips")]
    [InlineData("=", SqlBinaryOperator.Equal)]
    [InlineData("<>", SqlBinaryOperator.NotEqual)]
    [InlineData("!=", SqlBinaryOperator.NotEqual)]
    [InlineData("<", SqlBinaryOperator.LessThan)]
    [InlineData("<=", SqlBinaryOperator.LessOrEqual)]
    [InlineData(">", SqlBinaryOperator.GreaterThan)]
    [InlineData(">=", SqlBinaryOperator.GreaterOrEqual)]
    public void Parse_QuantifiedComparison_ShouldRetainCompleteTree(string op, SqlBinaryOperator expected)
    {
        foreach (var quantifier in Enum.GetValues<SqlComparisonQuantifier>())
        {
            // Arrange / Act
            var parsed = Parse($"SELECT (a + 1) {op} {quantifier} /* trivia */ ((SELECT b FROM u) UNION ALL SELECT c FROM v) IS UNKNOWN FROM t;");
            var test = parsed.Columns[0].Expression.ShouldBeOfType<SqlBooleanTestExpression>();
            var comparison = test.Operand.ShouldBeOfType<SqlQuantifiedComparisonExpression>();
            var canonical = SqlExpressionRenderer.Render(parsed);

            // Assert
            comparison.Operator.ShouldBe(expected);
            comparison.Quantifier.ShouldBe(quantifier);
            comparison.Subquery.ShouldBeOfType<SqlSetOperationExpression>();
            SqlTreeDump.Dump(Parse(canonical)).ShouldBe(SqlTreeDump.Dump(parsed));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Quantified comparisons: excluded positions retain capability refusals")]
    [InlineData("UPDATE t SET a = 1 = ANY (SELECT b FROM u);", "SUBQUERY in UPDATE")]
    [InlineData("INSERT INTO t VALUES (1 = SOME (SELECT b FROM u));", "SUBQUERY in INSERT VALUES")]
    [InlineData("DELETE FROM t RETURNING 1 = ALL (SELECT b FROM u);", "SUBQUERY in RETURNING")]
    [InlineData("SELECT 1 = ANY (1, 2);", "ANY array comparison")]
    [InlineData("SELECT 1 = ALL (@values);", "ALL array comparison")]
    [InlineData("SELECT 1 = SOME ((@values));", "SOME array comparison")]
    [InlineData("SELECT (1,2) = ANY (SELECT a,b FROM t);", "row")]
    public void Parse_ExcludedForm_ShouldRetainUnsupportedDiagnostic(string sql, string construct)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(d => d.Code == (construct == "row" ? "CS00003" : "CK00001"));
        if (construct != "row") { statement.Diagnostics.ShouldContain(d => d.Message != null && d.Message.Contains(construct, StringComparison.OrdinalIgnoreCase)); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Quantified comparisons: missing closing delimiters name the subquery")]
    [InlineData("ANY")]
    [InlineData("SOME")]
    [InlineData("ALL")]
    public void Parse_UnclosedSubquery_ShouldUseGenericDelimiterDiagnostic(string quantifier)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT 1 = {quantifier} (SELECT 1");

        // Assert
        statement.Diagnostics.ShouldContain(d => d.Code == "CS00003" && d.Message != null &&
            d.Message.Contains("after the subquery", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Quantified comparisons: contextual names retain canonical persisted quoting")]
    public void Render_ContextualNames_ShouldKeepCanonicalIdentifierText()
    {
        // Arrange / Act
        var query = Parse("SELECT 1 = any, some FROM t;");
        string canonical = SqlExpressionRenderer.Render(query);

        // Assert
        canonical.ShouldContain("\"any\"", Case.Sensitive);
        canonical.ShouldContain("\"some\"", Case.Sensitive);
        SqlTreeDump.Dump(Parse(canonical)).ShouldBe(SqlTreeDump.Dump(query));
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Quantified comparisons: complete predicate and subquery depth are retained")]
    public void Parse_CompletePredicateAndDepth_ShouldRetainGrammarAndLimit()
    {
        // Arrange / Act
        var chained = Parse("SELECT 1 = ANY (SELECT 1) = TRUE;");
        string nested = "1";
        for (int level = 0; level < 33; level++) { nested = $"1 = ANY (SELECT {nested})"; }
        var tooDeep = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT {nested};");

        // Assert
        chained.Columns[0].Expression.ShouldBeOfType<SqlBinaryExpression>().Left.ShouldBeOfType<SqlQuantifiedComparisonExpression>();
        tooDeep.Diagnostics.ShouldContain(d => d.Code == "CK00001");
    }

    private static SqlSelectExpression Parse(string sql)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        statement.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        return statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
    }
}
