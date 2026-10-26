using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlTruncateParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - TRUNCATE: table lists and policies round trip")]
    [InlineData("TRUNCATE t", false, false, 1)]
    [InlineData("TRUNCATE TABLE ONLY dbo.t, ONLY \"odd table\" RESTART IDENTITY CASCADE", true, true, 2)]
    [InlineData("TRUNCATE t, u CONTINUE IDENTITY RESTRICT", false, false, 2)]
    [InlineData("truncate t cascade", false, true, 1)]
    public void Parse_TruncateListAndPolicies_ShouldRoundTrip(string sql, bool restart, bool cascade, int count)
    {
        // Arrange / Act
        var statement = Parse(sql);
        var truncate = statement.SqlExpression.ShouldBeOfType<SqlTruncateExpression>();

        // Assert
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        truncate.CommandType.ShouldBe(SqlQueryCommandType.Truncate);
        truncate.Tables.Count.ShouldBe(count);
        truncate.RestartIdentity.ShouldBe(restart);
        truncate.Cascade.ShouldBe(cascade);
        var rendered = SqlExpressionRenderer.Render(truncate);
        var reparsed = Parse(rendered);
        reparsed.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        SqlExpressionRenderer.Render(reparsed.SqlExpression.ShouldBeOfType<SqlTruncateExpression>()).ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - TRUNCATE: malformed suffixes never execute a prefix")]
    [InlineData("TRUNCATE")]
    [InlineData("TRUNCATE TABLE")]
    [InlineData("TRUNCATE TABLE ONLY")]
    [InlineData("TRUNCATE t,")]
    [InlineData("TRUNCATE t RESTART")]
    [InlineData("TRUNCATE t RESTART IDENTITY CONTINUE IDENTITY")]
    [InlineData("TRUNCATE t CASCADE RESTRICT")]
    [InlineData("TRUNCATE t CASCADE RESTART IDENTITY")]
    [InlineData("TRUNCATE t WHERE id = 1")]
    [InlineData("TRUNCATE t AS x")]
    public void Parse_TruncateMalformed_ShouldReportSyntax(string sql)
    {
        // Arrange / Act
        var statement = Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Code == "CS00003");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - CTAS remains refused in SQ-T1")]
    public void Parse_CreateTableAsSelect_ShouldRetainExistingRefusal()
    {
        // Arrange / Act
        var statement = Parse("CREATE TABLE t AS SELECT 1");

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS00003");
    }

    private static SqlQueryStatement Parse(string sql) => (SqlQueryStatement)new SqlQueryParser().Parse(sql);
}
