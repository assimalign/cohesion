using System.Linq;

using Assimalign.Cohesion.Database.Types;

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

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - ORDER BY: retains explicit NULL placement")]
    [InlineData("SELECT age FROM t ORDER BY age NULLS FIRST;", SqlNullPlacement.First)]
    [InlineData("SELECT age FROM t ORDER BY age ASC NULLS LAST;", SqlNullPlacement.Last)]
    [InlineData("SELECT age FROM t ORDER BY age DESC NULLS FIRST;", SqlNullPlacement.First)]
    [InlineData("SELECT age FROM t ORDER BY age DESC NULLS LAST;", SqlNullPlacement.Last)]
    [InlineData("SELECT age AS years FROM t ORDER BY years ASC nulls /* trivia */ first;", SqlNullPlacement.First)]
    [InlineData("SELECT age FROM t ORDER BY 1 NULLS LAST, age DESC;", SqlNullPlacement.Last)]
    [InlineData("SELECT age, COUNT(*) FROM t GROUP BY age ORDER BY 2 DESC NULLS FIRST;", SqlNullPlacement.First)]
    [InlineData("SELECT age FROM t WHERE EXISTS (SELECT id FROM u ORDER BY 1 NULLS LAST);", SqlNullPlacement.Last)]
    [InlineData("SELECT age FROM t ORDER BY age;", SqlNullPlacement.Default)]
    public void Parse_ExplicitNullPlacement_ShouldPreservePlacement(string sql, SqlNullPlacement expected)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
        var query = select.Where is SqlExistsExpression exists ? exists.Subquery : select;
        query.OrderBy[0].NullPlacement.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - ORDER BY: malformed NULL placement reports CS00003")]
    [InlineData("NULLS")]
    [InlineData("NULLS MIDDLE")]
    public void Parse_MalformedNullPlacement_ShouldReportSyntaxError(string suffix)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse($"SELECT a FROM t ORDER BY a {suffix};");
        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS00003");
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Code == "CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - ORDER BY: canonical text preserves written placement and identifier quoting")]
    [InlineData("SELECT a FROM t ORDER BY a NULLS FIRST", "SELECT a FROM t ORDER BY a NULLS FIRST")]
    [InlineData("SELECT a FROM t ORDER BY a DESC NULLS LAST", "SELECT a FROM t ORDER BY a DESC NULLS LAST")]
    [InlineData("SELECT a FROM t ORDER BY a ASC NULLS LAST", "SELECT a FROM t ORDER BY a NULLS LAST")]
    [InlineData("SELECT a FROM t ORDER BY a DESC NULLS FIRST", "SELECT a FROM t ORDER BY a DESC NULLS FIRST")]
    [InlineData("SELECT a FROM t ORDER BY a", "SELECT a FROM t ORDER BY a")]
    [InlineData("SELECT nulls, first, last FROM t ORDER BY last", "SELECT \"nulls\", first, last FROM t ORDER BY last")]
    public void Render_NullPlacement_ShouldPreserveSyntaxAndQuoting(string sql, string expected)
    {
        // Arrange
        var parsed = (SqlQueryStatement)new SqlQueryParser().Parse(sql + ";");
        parsed.Diagnostics.ShouldBeEmpty();
        var query = parsed.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
        // Act
        string rendered = SqlExpressionRenderer.Render(query);
        var restored = (SqlQueryStatement)new SqlQueryParser().Parse(rendered + ";");
        // Assert
        rendered.ShouldBe(expected);
        restored.Diagnostics.ShouldBeEmpty();
        SqlTreeDump.Dump(restored.SqlExpression.ShouldBeOfType<SqlSelectExpression>()).ShouldBe(SqlTreeDump.Dump(query));
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

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "CK00001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Message!.ShouldContain("select-list ordinals in GROUP BY are not supported");
    }

    /// <summary>Preserves supported subquery composition and identifiers near ordering boundaries.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - ORDER BY: Keeps supported subqueries and ordinary names")]
    [InlineData("SELECT nulls AS first FROM t ORDER BY nulls;")]
    [InlineData("SELECT nulls, first, last FROM t ORDER BY last;")]
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

    /// <summary>Retains derived output ordering syntax for ordinary alias and ordinal binding.</summary>
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - ORDER BY: Retains derived output keys for binding")]
    [InlineData("SELECT q.years FROM (SELECT age AS years FROM t) q ORDER BY years;")]
    [InlineData("SELECT q.years FROM (SELECT age AS years FROM t) q ORDER BY 1;")]
    public void Parse_DerivedTableOutputOrdering_PreservesOrderingKey(string sql)
    {
        var statement = new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        ((SqlQueryStatement)statement).SqlExpression.ShouldBeOfType<SqlSelectExpression>().OrderBy.ShouldHaveSingleItem();
    }
}
