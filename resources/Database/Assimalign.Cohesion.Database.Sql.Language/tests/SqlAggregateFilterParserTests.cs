using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlAggregateFilterParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - FILTER: complete calls survive canonical rendering")]
    [InlineData("COUNT(*) FILTER (WHERE flag)")]
    [InlineData("SUM(DISTINCT v) FILTER (WHERE v > 0)")]
    [InlineData("AVG(ALL v ORDER BY id) FILTER (WHERE NULL)")]
    [InlineData("STRING_AGG(label, ',' ORDER BY id DESC) FILTER (WHERE flag)")]
    [InlineData("LISTAGG(DISTINCT label, ',') WITHIN GROUP (ORDER BY label) FILTER (WHERE flag)")]
    [InlineData("EVERY(flag) FILTER (WHERE EXISTS (SELECT 1 WHERE flag))")]
    [InlineData("VAR_SAMP(v) FILTER (WHERE v > 0)")]
    [InlineData("UPPER(label) FILTER (WHERE flag)")]
    public void Parse_FilteredCall_ShouldRetainConditionLocationAndDepth(string expression)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("SELECT " + expression + " FROM t;");
        var call = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression.ShouldBeOfType<SqlFunctionCallExpression>();
        var rendered = SqlExpressionRenderer.Render(call);
        var reloaded = (SqlQueryStatement)new SqlQueryParser().Parse("SELECT " + rendered + " FROM t;");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        call.Filter.ShouldNotBeNull();
        call.Location!.Start.ShouldBe(7);
        call.Location.End.ShouldBe(7 + expression.Length);
        call.Depth.ShouldBeGreaterThan(call.Filter!.Depth);
        reloaded.Diagnostics.ShouldBeEmpty();
        SqlTreeDump.Dump(reloaded.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression).ShouldBe(SqlTreeDump.Dump(call));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - FILTER: malformed clauses report syntax diagnostics")]
    [InlineData("SELECT COUNT(*) FILTER (TRUE) FROM t;")]
    [InlineData("SELECT COUNT(*) FILTER (WHERE) FROM t;")]
    [InlineData("SELECT COUNT(*) FILTER (WHERE TRUE FROM t;")]
    [InlineData("SELECT COUNT(*) FILTER (WHERE TRUE) FILTER (WHERE FALSE) FROM t;")]
    public void Parse_MalformedFilter_ShouldReportSyntaxError(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Code != "CK00001");
    }
}
