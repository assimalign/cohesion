using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlWindowParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Windows: inline and named specifications render and reparse")]
    [InlineData("SELECT ROW_NUMBER() OVER(),RANK() OVER(PARTITION BY p ORDER BY k DESC NULLS LAST) FROM t;")]
    [InlineData("SELECT LAG(v,n,0) OVER b FROM t WINDOW a AS(PARTITION BY p),b AS(a ORDER BY k NULLS FIRST);")]
    [InlineData("SELECT LEAD(v) OVER (a ORDER BY k) FROM t WINDOW a AS (PARTITION BY p);")]
    [InlineData("SELECT PERCENT_RANK() OVER w,CUME_DIST() OVER w,NTILE(3) OVER w FROM t WINDOW w AS(ORDER BY k);")]
    [InlineData("SELECT RANK() OVER \"a\" FROM t WINDOW \"a\" AS (ORDER BY k);")]
    [InlineData("CREATE VIEW v AS SELECT ROW_NUMBER() OVER w AS n FROM t WINDOW w AS(ORDER BY k);")]
    public void Parse_WindowForms_ShouldRoundTrip(string sql)
    {
        // Arrange / Act
        var first = new SqlQueryParser().Parse(sql).ShouldBeOfType<SqlQueryStatement>();
        first.Diagnostics.ShouldBeEmpty(string.Join("; ", first.Diagnostics.Select(d => d.Message)));
        string Render(SqlQueryExpression expression) => expression is SqlCreateViewExpression view
            ? SqlExpressionRenderer.Render(view.Query) : SqlExpressionRenderer.Render((SqlQueryBodyExpression)expression);
        string rendered = Render(first.SqlExpression);
        var second = new SqlQueryParser().Parse(rendered + ";").ShouldBeOfType<SqlQueryStatement>();
        // Assert
        second.Diagnostics.ShouldBeEmpty();
        Render(second.SqlExpression).ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Windows: deferred frames, value functions and aggregate windows identify their form")]
    [InlineData("SELECT ROW_NUMBER() OVER(ROWS UNBOUNDED PRECEDING)", "ROWS")]
    [InlineData("SELECT ROW_NUMBER() OVER(RANGE UNBOUNDED PRECEDING)", "RANGE")]
    [InlineData("SELECT ROW_NUMBER() OVER(GROUPS CURRENT ROW)", "GROUPS")]
    [InlineData("SELECT ROW_NUMBER() OVER(EXCLUDE TIES)", "EXCLUDE")]
    [InlineData("SELECT SUM(v) OVER() FROM t", "SUM")]
    [InlineData("SELECT FIRST_VALUE(v) OVER() FROM t", "FIRST_VALUE")]
    [InlineData("SELECT LAST_VALUE(v) OVER() FROM t", "LAST_VALUE")]
    [InlineData("SELECT NTH_VALUE(v,2) OVER() FROM t", "NTH_VALUE")]
    public void Parse_DeferredForms_ShouldNameUnsupportedForm(string sql, string form)
    {
        // Arrange / Act
        var parsed = new SqlQueryParser().Parse(sql + ";");
        // Assert
        parsed.Diagnostics.ShouldContain(d => d.Code == "CK00001" && d.Message!.Contains(form, System.StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Windows: incomplete grammar is a syntax error")]
    [InlineData("SELECT RANK() OVER")]
    [InlineData("SELECT RANK() OVER(")]
    [InlineData("SELECT RANK() OVER(PARTITION BY)")]
    [InlineData("SELECT RANK() OVER(ORDER BY)")]
    [InlineData("SELECT 1 WINDOW w AS")]
    [InlineData("SELECT 1 WINDOW w AS(),")]
    public void Parse_MalformedWindows_ShouldRefuse(string sql)
    {
        // Arrange / Act / Assert
        new SqlQueryParser().Parse(sql + ";").Diagnostics.ShouldContain(d => d.Severity == DiagnosticSeverity.Error);
    }
}
