using System.Linq;

using Assimalign.Cohesion.Database.Language;

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
    [InlineData("SELECT COUNT(DISTINCT amount) FROM t;", "DISTINCT")]
    [InlineData("SELECT SUM(DISTINCT amount) FROM t;", "DISTINCT")]
    [InlineData("SELECT AVG(DISTINCT amount) FROM t;", "DISTINCT")]
    [InlineData("SELECT MIN(DISTINCT amount) FROM t;", "DISTINCT")]
    [InlineData("SELECT MAX(DISTINCT amount) FROM t;", "DISTINCT")]
    [InlineData("select count(/* trivia */ distinct amount) from t;", "distinct")]
    [InlineData("SELECT COUNT(ALL amount) FROM t;", "ALL")]
    [InlineData("SELECT SUM(ALL amount) FROM t;", "ALL")]
    [InlineData("SELECT SUM(amount ORDER BY id) FROM t;", "ORDER")]
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
    [InlineData("SELECT category FROM t GROUP BY category HAVING COUNT(DISTINCT amount) > 0;", "DISTINCT")]
    [InlineData("SELECT category FROM t GROUP BY category ORDER BY COUNT(DISTINCT amount);", "DISTINCT")]
    public void Parse_UnsupportedAggregateForm_ReportsModelDiagnostic(string sql, string locationText)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "COHDBL001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Message!.ShouldContain("SQL");
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe(locationText);
        statement.Diagnostics.ShouldNotContain(item => item.Code == "SQL0002");
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
    [InlineData("SELECT 'COUNT(DISTINCT amount) GROUPING SETS ROLLUP() WITHIN GROUP FILTER() OVER' FROM t;")]
    [InlineData("SELECT \"ROLLUP\", \"CUBE\", \"GROUPING\", \"FILTER\" FROM t;")]
    [InlineData("SELECT amount FROM t /* GROUPING SETS ROLLUP() FILTER() WITHIN GROUP */;")]
    [InlineData("SELECT DISTINCT COUNT(amount), SUM(amount) FROM t;")]
    public void Parse_ExtensionNamesOutsideUnsupportedSyntax_ReportsNoError(string sql)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
    }
}
