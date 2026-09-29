using System.Linq;

using Assimalign.Cohesion.Database.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Verifies ordering expression syntax and diagnostics at unsupported ordering boundaries.
/// </summary>
public sealed class SqlOrderByParserTests
{
    /// <summary>Preserves alias-bearing keys and their ordering and pagination modifiers.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - ORDER BY: Preserves projection aliases and scalar composition")]
    [InlineData("years")]
    [InlineData("years + 1")]
    [InlineData("ABS(years)")]
    [InlineData("CASE WHEN years IS NULL THEN 0 ELSE years END")]
    [InlineData("years COLLATE binary")]
    public void Parse_ProjectionAliasOrdering_PreservesOrderingExpression(string key)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(
            $"SELECT age AS years FROM t ORDER BY {key} DESC, age ASC LIMIT 2 OFFSET 1;");

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
        select.Columns.Single().Alias.ShouldBe("years");
        select.OrderBy.Count.ShouldBe(2);
        select.OrderBy[0].IsDescending.ShouldBeTrue();
        select.OrderBy[1].IsDescending.ShouldBeFalse();
        select.Limit.ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe("2");
        select.Offset.ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe("1");
    }

    /// <summary>Leaves catalog-dependent ordinal validation to the planner without folding numeric syntax.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - ORDER BY: Retains numeric keys for ordinal validation")]
    [InlineData("1", "1", SqlLiteralType.Integer)]
    [InlineData("+1", "1", SqlLiteralType.Integer)]
    [InlineData("+ /* trivia */ 2", "2", SqlLiteralType.Integer)]
    [InlineData("0", "0", SqlLiteralType.Integer)]
    [InlineData("999999999999999999999999", "999999999999999999999999", SqlLiteralType.Integer)]
    [InlineData("1.5", "1.5", SqlLiteralType.Float)]
    [InlineData("+1.0", "1.0", SqlLiteralType.Float)]
    [InlineData("1e0", "1e0", SqlLiteralType.Float)]
    public void Parse_NumericOrderingKey_LeavesOrdinalValidationToPlanner(string key, string value, SqlLiteralType type)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT age FROM t ORDER BY {key} DESC;");

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        var order = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().OrderBy.Single();
        var literal = order.Expression.ShouldBeOfType<SqlLiteralExpression>();
        literal.Value.ShouldBe(value);
        literal.LiteralType.ShouldBe(type);
        order.IsDescending.ShouldBeTrue();
    }

    /// <summary>Retains the negative sign needed to reject a negative ordinal precisely.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Language] - ORDER BY: Retains negative ordinal sign")]
    public void Parse_NegativeOrderingKey_PreservesNegationForPlanner()
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("SELECT age FROM t ORDER BY -1;");

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        var unary = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>()
            .OrderBy.Single().Expression.ShouldBeOfType<SqlUnaryExpression>();
        unary.Operator.ShouldBe(SqlUnaryOperator.Negate);
        unary.Operand.ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe("1");
    }

    /// <summary>Keeps compound constant expressions distinct from standalone output positions.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - ORDER BY: Keeps arithmetic distinct from ordinals")]
    [InlineData("1 + 1")]
    [InlineData("+1 + 1")]
    [InlineData("(1 + 1)")]
    public void Parse_ConstantArithmetic_PreservesBinaryExpression(string key)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT age FROM t ORDER BY {key};");

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        var binary = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>()
            .OrderBy.Single().Expression.ShouldBeOfType<SqlBinaryExpression>();
        binary.Operator.ShouldBe(SqlBinaryOperator.Add);
        binary.Left.ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe("1");
        binary.Right.ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe("1");
    }

    /// <summary>Reports the exact unsupported NULL placement modifier rather than ignoring it.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - ORDER BY: Rejects explicit NULL placement")]
    [InlineData("SELECT age FROM t ORDER BY age NULLS FIRST;", "NULLS FIRST")]
    [InlineData("SELECT age FROM t ORDER BY age DESC NULLS LAST;", "NULLS LAST")]
    [InlineData("SELECT age AS years FROM t ORDER BY years ASC nulls /* trivia */ first;", "nulls /* trivia */ first")]
    [InlineData("SELECT age FROM t ORDER BY 1 NULLS LAST, age DESC;", "NULLS LAST")]
    [InlineData("SELECT age, COUNT(*) FROM t GROUP BY age ORDER BY 2 DESC NULLS FIRST;", "NULLS FIRST")]
    [InlineData("SELECT age FROM t WHERE EXISTS (SELECT id FROM u ORDER BY 1 NULLS LAST);", "NULLS LAST")]
    public void Parse_ExplicitNullPlacement_ReportsPreciseModelDiagnostic(string sql, string location)
    {
        var statement = new SqlQueryParser().Parse(sql);

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "COHDBL001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Message!.ShouldContain("NULLS FIRST and NULLS LAST are not supported");
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe(location);
    }

    /// <summary>Restricts select-list ordinal syntax to ORDER BY.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - GROUP BY: Rejects ordinal syntax outside ORDER BY")]
    [InlineData("1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("1.0")]
    [InlineData("-1.5")]
    [InlineData("(1)")]
    public void Parse_GroupingOrdinal_ReportsModelDiagnostic(string key)
    {
        var statement = new SqlQueryParser().Parse($"SELECT age, COUNT(*) FROM t GROUP BY {key};");

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "COHDBL001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Message!.ShouldContain("select-list ordinals in GROUP BY are not supported");
    }

    /// <summary>Preserves supported subquery composition and identifiers near ordering boundaries.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - ORDER BY: Keeps supported subqueries and ordinary names")]
    [InlineData("SELECT nulls AS first FROM t ORDER BY nulls;")]
    [InlineData("SELECT age AS years FROM t WHERE id IN (SELECT id AS local_id FROM u ORDER BY local_id LIMIT 2) ORDER BY years;")]
    [InlineData("SELECT age AS years FROM t WHERE EXISTS (SELECT id FROM u ORDER BY 1 DESC LIMIT 1) ORDER BY 1;")]
    [InlineData("SELECT age, (SELECT MAX(id) FROM u) AS greatest FROM t ORDER BY greatest, age;")]
    [InlineData("SELECT age FROM t ORDER BY (SELECT MAX(id) FROM u), age;")]
    [InlineData("SELECT COUNT(*) FROM t GROUP BY 1 + 1;")]
    public void Parse_SupportedOrderingComposition_ReportsNoError(string sql)
    {
        var statement = new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
    }

    /// <summary>Rejects ordering over a derived table until that relation form executes.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - ORDER BY: Rejects derived-table output ordering")]
    [InlineData("SELECT q.years FROM (SELECT age AS years FROM t) q ORDER BY years;")]
    [InlineData("SELECT q.years FROM (SELECT age AS years FROM t) q ORDER BY 1;")]
    public void Parse_DerivedTableOutputOrdering_ReportsModelDiagnostic(string sql)
    {
        var statement = new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldContain(item => item.Code == "COHDBL001" &&
            item.Message!.Contains("derived-table SUBQUERY"));
    }
}
