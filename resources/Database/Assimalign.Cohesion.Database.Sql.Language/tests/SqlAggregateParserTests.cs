using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Verifies aggregate syntax and the explicit boundary of executable aggregation.
/// </summary>
public class SqlAggregateParserTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Aggregates: Preserves executable argument expressions")]
    [InlineData("COUNT")]
    [InlineData("SUM")]
    [InlineData("AVG")]
    [InlineData("MIN")]
    [InlineData("MAX")]
    public void Parse_AggregateExpression_PreservesArgumentAndComposition(string name)
    {
        var sql = $"SELECT category, LOWER(region), {name}(amount + 2) AS result " +
            $"FROM t WHERE amount > 0 GROUP BY category, LOWER(region) HAVING {name}(amount + 2) > 5 " +
            $"ORDER BY {name}(amount + 2) DESC LIMIT 2 OFFSET 1;";

        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
        select.GroupBy.Count.ShouldBe(2);
        select.GroupBy[1].ShouldBeOfType<SqlFunctionCallExpression>().FunctionName.ShouldBe("LOWER");
        var aggregate = select.Columns[2].Expression.ShouldBeOfType<SqlFunctionCallExpression>();
        aggregate.FunctionName.ShouldBe(name);
        aggregate.Arguments.Single().ShouldBeOfType<SqlBinaryExpression>().Operator.ShouldBe(SqlBinaryOperator.Add);
        select.Having.ShouldBeOfType<SqlBinaryExpression>().Operator.ShouldBe(SqlBinaryOperator.GreaterThan);
        select.OrderBy.Single().IsDescending.ShouldBeTrue();
        select.Limit.ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe("2");
        select.Offset.ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe("1");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Aggregates: Rejects unsupported extensions explicitly")]
    [InlineData("SELECT category, COUNT(*) FROM t GROUP BY ROLLUP(category);", "ROLLUP")]
    [InlineData("SELECT category, COUNT(*) FROM t GROUP BY CUBE(category);", "CUBE")]
    [InlineData("SELECT category, COUNT(*) FROM t GROUP BY GROUPING SETS ((category), ());", "GROUPING SETS")]
    [InlineData("select category, count(*) from t group by grouping /* trivia */ sets ((category));", "grouping /* trivia */ sets")]
    [InlineData("SELECT category, COUNT(*) FROM t GROUP BY category, ROLLUP(region);", "ROLLUP")]
    [InlineData("SELECT COUNT(*) FROM t GROUP BY ();", "()")]
    [InlineData("SELECT COUNT(*) FROM t GROUP BY (/* trivia */);", "(/* trivia */)")]
    [InlineData("SELECT category, COUNT(*) FROM t GROUP BY category, ();", "()")]
    [InlineData("SELECT GROUPING(category) FROM t GROUP BY category;", "GROUPING")]
    [InlineData("SELECT GROUPING_ID(category) FROM t GROUP BY category;", "GROUPING_ID")]
    [InlineData("SELECT SUM(amount) FILTER (WHERE amount > 0) FROM t;", "FILTER")]
    [InlineData("SELECT SUM(amount) OVER () FROM t;", "OVER")]
    [InlineData("SELECT ROW_NUMBER() FROM t;", "ROW_NUMBER")]
    [InlineData("SELECT RANK() FROM t;", "RANK")]
    [InlineData("SELECT PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY amount) FROM t;", "WITHIN GROUP")]
    [InlineData("SELECT PERCENTILE_DISC(0.5) WITHIN GROUP (ORDER BY amount) FROM t;", "WITHIN GROUP")]
    [InlineData("SELECT MODE() WITHIN GROUP (ORDER BY amount) FROM t;", "WITHIN GROUP")]
    [InlineData("SELECT \"LISTAGG\"(name) WITHIN GROUP (ORDER BY id) FROM t;", "WITHIN GROUP")]
    public void Parse_UnsupportedAggregateForm_ReportsModelDiagnostic(string sql, string locationText)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "COHDBL001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Message!.ShouldContain("SQL");
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe(locationText);
        statement.Diagnostics.ShouldNotContain(item => item.Code == "SQL0002");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Aggregates: quantifiers and input ordering survive rendering")]
    [InlineData("COUNT(DISTINCT amount)", SqlSetQuantifier.Distinct, 0)]
    [InlineData("SUM(DISTINCT amount)", SqlSetQuantifier.Distinct, 0)]
    [InlineData("AVG(DISTINCT amount)", SqlSetQuantifier.Distinct, 0)]
    [InlineData("MIN(DISTINCT amount)", SqlSetQuantifier.Distinct, 0)]
    [InlineData("MAX(DISTINCT amount)", SqlSetQuantifier.Distinct, 0)]
    [InlineData("count(/* trivia */ distinct amount)", SqlSetQuantifier.Distinct, 0)]
    [InlineData("COUNT(ALL amount)", SqlSetQuantifier.All, 0)]
    [InlineData("SUM(ALL amount)", SqlSetQuantifier.All, 0)]
    [InlineData("SUM(amount ORDER BY id)", SqlSetQuantifier.Default, 1)]
    [InlineData("STRING_AGG(name, ', ')", SqlSetQuantifier.Default, 0)]
    [InlineData("STRING_AGG(name, ', ' ORDER BY id DESC, name)", SqlSetQuantifier.Default, 2)]
    [InlineData("STRING_AGG(DISTINCT name, ',' ORDER BY name)", SqlSetQuantifier.Distinct, 1)]
    [InlineData("LISTAGG(name, ',') WITHIN GROUP (ORDER BY id)", SqlSetQuantifier.Default, 1)]
    [InlineData("LISTAGG(name) WITHIN GROUP (ORDER BY id)", SqlSetQuantifier.Default, 1)]
    [InlineData("LISTAGG(DISTINCT name, ',') WITHIN GROUP (ORDER BY name)", SqlSetQuantifier.Distinct, 1)]
    [InlineData("\"listagg\"(name) WITHIN GROUP (ORDER BY id)", SqlSetQuantifier.Default, 1)]
    [InlineData("UPPER(ALL name ORDER BY id)", SqlSetQuantifier.All, 1)]
    [InlineData("\"LISTAGG\"(name)", SqlSetQuantifier.Default, 0)]
    [InlineData("\"LISTAGG\"(name ORDER BY id)", SqlSetQuantifier.Default, 1)]
    [InlineData("\"EXTRACT\"(DISTINCT 'YEAR', d)", SqlSetQuantifier.Distinct, 0)]
    [InlineData("\"POSITION\"(DISTINCT a, b)", SqlSetQuantifier.Distinct, 0)]
    [InlineData("\"SUBSTRING\"(ALL a, 1, 2)", SqlSetQuantifier.All, 0)]
    [InlineData("\"TRIM\"(a ORDER BY b)", SqlSetQuantifier.Default, 1)]
    [InlineData("\"CURRENT_DATE\"(ALL a)", SqlSetQuantifier.All, 0)]
    [InlineData("\"EXTRACT\"('YEAR', d ORDER BY d)", SqlSetQuantifier.Default, 1)]
    [InlineData("\"POSITION\"(a, b ORDER BY b)", SqlSetQuantifier.Default, 1)]
    public void Parse_AggregateModifiers_ShouldRetainCompleteTree(string expression, SqlSetQuantifier quantifier, int keys)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT {expression} FROM t HAVING COUNT(DISTINCT amount) > 0 ORDER BY SUM(ALL amount);");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var call = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression.ShouldBeOfType<SqlFunctionCallExpression>();
        call.Location!.Start.ShouldBe(7);
        call.Location.End.ShouldBe(7 + expression.Length);
        call.Quantifier.ShouldBe(quantifier);
        call.OrderBy.Count.ShouldBe(keys);
        var rendered = SqlExpressionRenderer.Render(call);
        var reloaded = (SqlQueryStatement)new SqlQueryParser().Parse($"SELECT {rendered} FROM t;");
        reloaded.Diagnostics.ShouldBeEmpty(rendered);
        SqlTreeDump.Dump(reloaded.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression).ShouldBe(SqlTreeDump.Dump(call));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Aggregates: malformed modifiers report SQL0003")]
    [InlineData("SELECT COUNT(DISTINCT *) FROM t;")]
    [InlineData("SELECT COUNT(ALL *) FROM t;")]
    [InlineData("SELECT COUNT(* ORDER BY id) FROM t;")]
    [InlineData("SELECT COUNT(DISTINCT) FROM t;")]
    [InlineData("SELECT COUNT(ORDER BY id) FROM t;")]
    [InlineData("SELECT LISTAGG(name, ',') FROM t;")]
    [InlineData("SELECT LISTAGG(name, ',' ORDER BY id) WITHIN GROUP (ORDER BY name) FROM t;")]
    [InlineData("SELECT STRING_AGG(name, ',' ORDER BY id FROM t;")]
    public void Parse_InvalidAggregateSyntax_ShouldReportSyntaxError(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);
        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "SQL0003");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - LISTAGG: ON OVERFLOW is one contextual refusal")]
    [InlineData("TRUNCATE '...' WITH COUNT")]
    [InlineData("ERROR")]
    public void Parse_ListaggOverflow_ShouldReportOneCapabilityError(string overflow)
    {
        // Arrange
        string sql = $"SELECT LISTAGG(name, ',' ON OVERFLOW {overflow}) WITHIN GROUP (ORDER BY id) FROM t;";
        // Act
        var statement = new SqlQueryParser().Parse(sql);
        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("COHDBL001");
        sql[error.Start!.Value..error.End!.Value].ShouldBe("ON OVERFLOW");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Language] - Aggregate ordering: sort operands obey the expression depth limit")]
    public void Parse_DeepAggregateSortKey_ShouldRespectNestingBudget()
    {
        // Arrange
        var parser = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = 32 });
        string key = string.Concat(Enumerable.Repeat("g(", 30)) + "a" + new string(')', 30);
        // Act
        var atLimit = (SqlQueryStatement)parser.Parse($"SELECT f(a ORDER BY {key}) FROM t;");
        var pastLimit = parser.Parse($"SELECT f(a ORDER BY g({key})) FROM t;");
        // Assert
        atLimit.Diagnostics.ShouldBeEmpty();
        atLimit.ExpressionNestingDepth.ShouldBe(32);
        pastLimit.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "SQL0006");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Grouping: Rejects missing BY or grouping expressions")]
    [InlineData("SELECT COUNT(*) FROM t GROUP amount;", "Expected BY")]
    [InlineData("SELECT COUNT(*) FROM t GROUP BY;", "Expected a grouping expression")]
    [InlineData("SELECT COUNT(*) FROM t GROUP BY", "Expected a grouping expression")]
    [InlineData("SELECT COUNT(*) FROM t GROUP BY HAVING COUNT(*) > 0;", "Expected a grouping expression")]
    [InlineData("SELECT amount, COUNT(*) FROM t GROUP BY amount,;", "Expected a grouping expression")]
    [InlineData("SELECT amount, COUNT(*) FROM t GROUP BY ,amount;", "Expected a grouping expression")]
    [InlineData("SELECT amount, COUNT(*) FROM t GROUP BY amount,,region;", "Expected a grouping expression")]
    [InlineData("SELECT amount, COUNT(*) FROM t GROUP BY amount, ORDER BY amount;", "Expected a grouping expression")]
    [InlineData("SELECT COUNT(*) FROM t HAVING;", "Expected a predicate")]
    [InlineData("SELECT COUNT(*) FROM t HAVING", "Expected a predicate")]
    [InlineData("SELECT amount, COUNT(*) FROM t GROUP BY amount HAVING ORDER BY amount;", "Expected a predicate")]
    public void Parse_IncompleteGrouping_ReportsSyntaxDiagnostic(string sql, string message)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldContain(item =>
            item.Code == "SQL0003" && item.Severity == DiagnosticSeverity.Error && item.Message!.Contains(message));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Aggregates: Leaves ordinary identifiers and literals available")]
    [InlineData("SELECT rollup, cube, grouping, filter, within, ROW_NUMBER FROM t;")]
    [InlineData("SELECT grouping sets FROM t;")]
    [InlineData("SELECT \"ROLLUP\", \"CUBE\", \"GROUPING\", \"FILTER\" FROM t;")]
    [InlineData("SELECT amount FROM t /* GROUPING SETS ROLLUP() FILTER() WITHIN GROUP */;")]
    [InlineData("SELECT 'COUNT(DISTINCT amount) GROUPING SETS ROLLUP() WITHIN GROUP FILTER() OVER' FROM t;")]
    [InlineData("SELECT DISTINCT COUNT(amount), SUM(amount) FROM t;")]
    public void Parse_ExtensionNamesOutsideUnsupportedSyntax_ReportsNoError(string sql)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
    }
}
