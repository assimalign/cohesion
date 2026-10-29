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
    [InlineData("SELECT SUM(v) FROM first;")]
    [InlineData("SELECT SUM(v) FROM last;")]
    [InlineData("SELECT SUM(v) ignore FROM t;")]
    [InlineData("SELECT SUM(v) respect FROM t;")]
    [InlineData("SELECT PERCENT_RANK() OVER w,CUME_DIST() OVER w,NTILE(3) OVER w FROM t WINDOW w AS(ORDER BY k);")]
    [InlineData("SELECT RANK() OVER \"a\" FROM t WINDOW \"a\" AS (ORDER BY k);")]
    [InlineData("SELECT SUM(v) OVER() FROM t")]
    [InlineData("SELECT FIRST_VALUE(v) OVER() FROM t")]
    [InlineData("SELECT LAST_VALUE(v) OVER() FROM t")]
    [InlineData("SELECT NTH_VALUE(v,2) OVER() FROM t")]
    [InlineData("SELECT ROW_NUMBER() OVER(ROWS UNBOUNDED PRECEDING)")]
    [InlineData("SELECT ROW_NUMBER() OVER(RANGE UNBOUNDED PRECEDING)")]
    [InlineData("SELECT SUM(v) FILTER(WHERE v>0) OVER w,FIRST_VALUE(v) OVER w,LAST_VALUE(v) OVER w,NTH_VALUE(v,2) OVER w FROM t WINDOW w AS(ORDER BY k ROWS BETWEEN 3 PRECEDING AND 2 FOLLOWING)")]
    [InlineData("SELECT \"aggregate name\"(v) OVER(ORDER BY k ROWS 2 PRECEDING) FROM t;")]
    [InlineData("CREATE VIEW v AS SELECT ROW_NUMBER() OVER w AS n FROM t WINDOW w AS(ORDER BY k);")]
    public void Parse_WindowForms_ShouldRoundTrip(string sql)
    {
        // Arrange / Act
        var first = new SqlQueryParser().Parse(sql.TrimEnd(';') + ";").ShouldBeOfType<SqlQueryStatement>();
        first.Diagnostics.ShouldBeEmpty(string.Join("; ", first.Diagnostics.Select(d => d.Message)));
        string Render(SqlQueryExpression expression) => expression is SqlCreateViewExpression view
            ? SqlExpressionRenderer.Render(view.Query) : SqlExpressionRenderer.Render((SqlQueryBodyExpression)expression);
        string rendered = Render(first.SqlExpression);
        var second = new SqlQueryParser().Parse(rendered + ";").ShouldBeOfType<SqlQueryStatement>();
        // Assert
        second.Diagnostics.ShouldBeEmpty();
        Render(second.SqlExpression).ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Windows: deferred frame modes and value modifiers identify their form")]
    [InlineData("SELECT SUM(v) OVER(ORDER BY k RANGE 1 PRECEDING) FROM t", "RANGE with offsets")]
    [InlineData("SELECT NTH_VALUE(v,2) FROM LAST OVER() FROM t", "FROM LAST")]
    [InlineData("SELECT NTH_VALUE(v,2) FROM FIRST OVER() FROM t", "FROM FIRST")]
    [InlineData("SELECT FIRST_VALUE(v) IGNORE NULLS OVER() FROM t", "IGNORE NULLS")]
    [InlineData("SELECT LAST_VALUE(v) RESPECT NULLS OVER() FROM t", "RESPECT NULLS")]
    [InlineData("SELECT ROW_NUMBER() OVER(GROUPS CURRENT ROW)", "GROUPS")]
    [InlineData("SELECT ROW_NUMBER() OVER(EXCLUDE TIES)", "EXCLUDE")]
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
