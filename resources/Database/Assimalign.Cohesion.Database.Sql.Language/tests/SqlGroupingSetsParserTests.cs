using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlGroupingSetsParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Grouping sets: syntax survives canonical round trips")]
    [InlineData("()")]
    [InlineData("GROUPING SETS ((a,b), (a), (), ())")]
    [InlineData("a, ROLLUP(b,c)")]
    [InlineData("CUBE((a,b),c)")]
    [InlineData("DISTINCT ROLLUP(a,b), CUBE(a,c)")]
    [InlineData("ALL GROUPING /* comment */ SETS (ROLLUP(a+b,b),CUBE(c),())")]
    [InlineData("GROUPING SETS (ROLLUP(a,b),CUBE(c))")]
    [InlineData("GROUPING SETS ((a,b),GROUPING SETS ((b),()))")]
    [InlineData("ROLLUP((a,(b,c)))")]
    [InlineData("(a+b)*c, GROUPING SETS ((b,c),())")]
    [InlineData("((a)), ROLLUP(((b,c)))")]
    [InlineData("(ROLLUP(a,b))")]
    [InlineData("(SELECT MAX(x.a) FROM t x, t y), GROUPING SETS ((a),())")]
    [InlineData("GROUPING SETS (((SELECT MAX(x.a) FROM t x, t y)),())")]
    public void Parse_GroupingElements_ShouldRoundTrip(string grouping)
    {
        // Arrange / Act
        var parsed = Parse("SELECT COUNT(*) FROM t GROUP BY " + grouping + ";");
        string text = SqlExpressionRenderer.Render(parsed);
        var reloaded = Parse(text + ";");

        // Assert
        SqlTreeDump.Dump(reloaded).ShouldBe(SqlTreeDump.Dump(parsed));
        SqlExpressionRenderer.Render(reloaded).ShouldBe(text);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Grouping sets: malformed elements report syntax errors")]
    [InlineData("GROUPING SETS ()")]
    [InlineData("ROLLUP()")]
    [InlineData("CUBE(a,)")]
    [InlineData("GROUPING SETS ((a),")]
    [InlineData("DISTINCT")]
    public void Parse_MalformedElements_ShouldRefuse(string grouping)
    {
        // Arrange / Act
        var parsed = new SqlQueryParser().Parse("SELECT COUNT(*) FROM t GROUP BY " + grouping + ";");

        // Assert
        parsed.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Code == "CS00003");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Grouping sets: nesting shares the expression limit")]
    public void Parse_DeepGrouping_ShouldRefuseBeforeRecursionOverflows()
    {
        // Arrange / Act
        var parsed = new SqlQueryParser().Parse("SELECT COUNT(*) FROM t GROUP BY " +
            string.Concat(Enumerable.Repeat("GROUPING SETS (", 300)) + "a" + new string(')', 300) + ";");

        // Assert
        parsed.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS00006");
    }

    private static SqlSelectExpression Parse(string sql)
    {
        var parsed = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        parsed.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return parsed.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
    }
}
