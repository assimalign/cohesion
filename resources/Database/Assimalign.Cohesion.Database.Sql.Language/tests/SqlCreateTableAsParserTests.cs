using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlCreateTableAsParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - CTAS: query forms, names and data policy round trip")]
    [InlineData("CREATE TABLE t AS SELECT 1 AS n", true)]
    [InlineData("CREATE TABLE t (n) /* names */ AS SELECT 1 WITH DATA", true)]
    [InlineData("CREATE TABLE IF NOT EXISTS dbo.t (\"a b\",n) AS SELECT 'x',2 WITH DATA", true)]
    [InlineData("CREATE TABLE t (n) AS WITH q AS (SELECT 1 AS n) SELECT n FROM q WITH NO DATA", false)]
    [InlineData("CREATE TABLE t AS SELECT 1 AS n UNION ALL SELECT 2 ORDER BY n WITH NO DATA", false)]
    [InlineData("CREATE TABLE t AS SELECT (SELECT 1) AS n WITH NO DATA", false)]
    public void Parse_TableAsQuery_ShouldRoundTrip(string sql, bool data)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        var create = statement.SqlExpression.ShouldBeOfType<SqlCreateTableAsExpression>();
        create.WithData.ShouldBe(data);
        var rendered = SqlExpressionRenderer.Render(create);
        var roundtrip = (SqlQueryStatement)new SqlQueryParser().Parse(rendered);
        roundtrip.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        SqlExpressionRenderer.Render(roundtrip.SqlExpression.ShouldBeOfType<SqlCreateTableAsExpression>()).ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - CTAS: malformed forms refuse the whole statement")]
    [InlineData("CREATE TABLE t AS")]
    [InlineData("CREATE TABLE t () AS SELECT 1")]
    [InlineData("CREATE TABLE t (a,) AS SELECT 1")]
    [InlineData("CREATE TABLE t (a INT) AS SELECT 1")]
    [InlineData("CREATE TABLE t AS SELECT 1 WITH")]
    [InlineData("CREATE TABLE t AS SELECT 1 WITH NO")]
    [InlineData("CREATE TABLE t AS SELECT 1 WITH DATA WITH DATA")]
    [InlineData("CREATE TABLE t AS DELETE FROM s")]
    [InlineData("CREATE TABLE t AS WITH q AS (SELECT 1 AS n) DELETE FROM s")]
    public void Parse_MalformedTableAs_ShouldReportError(string sql)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(d => d.Severity == DiagnosticSeverity.Error);
    }
}
