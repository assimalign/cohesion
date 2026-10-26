using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlRightJoinParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - RIGHT groups retain association and round trip (#1525)")]
    [InlineData("SELECT * FROM a RIGHT JOIN b ON a.id = b.id")]
    [InlineData("SELECT * FROM a RIGHT OUTER JOIN b USING (id, name)")]
    [InlineData("SELECT * FROM (a LEFT JOIN b USING (id)) RIGHT JOIN c USING (id)")]
    [InlineData("SELECT * FROM a LEFT JOIN (b RIGHT JOIN c USING (id)) USING (id)")]
    [InlineData("SELECT * FROM a, (b RIGHT JOIN c USING (id)), d")]
    [InlineData("SELECT g.id FROM (a RIGHT JOIN b USING (id)) AS g")]
    [InlineData("SELECT g.x FROM (a RIGHT JOIN b USING (id)) AS g (x)")]
    public void Render_RightJoinTree_ShouldRoundTrip(string sql)
    {
        // Arrange
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        // Act
        var first = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
        string canonical = SqlExpressionRenderer.Render(first);
        var reloaded = (SqlQueryStatement)new SqlQueryParser().Parse(canonical);

        // Assert
        reloaded.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        SqlExpressionRenderer.Render(reloaded.SqlExpression.ShouldBeOfType<SqlSelectExpression>()).ShouldBe(canonical);
        SqlTreeDump.Dump(reloaded.SqlExpression.ShouldBeOfType<SqlSelectExpression>()).ShouldBe(SqlTreeDump.Dump(first));
        canonical.ShouldContain("RIGHT OUTER JOIN", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - RIGHT groups expose a typed right subtree (#1525)")]
    public void Parse_RightGroup_ShouldRetainComponentNamespaces()
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("SELECT * FROM a LEFT JOIN (b RIGHT JOIN c USING (id)) ON a.id = c.id");
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        // Assert
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var group = select.From.Single().Joins.Single().Table.ShouldBeOfType<SqlJoinedTableReference>();
        group.Item.Table.ShouldBeOfType<SqlTableReference>().TableName.ShouldBe("b");
        group.Item.Joins.Single().JoinType.ShouldBe(SqlJoinType.RightOuter);
        group.Item.Joins.Single().UsingColumns.ShouldBe(["id"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - RIGHT groups retain syntax and nesting guards (#1525)")]
    [InlineData("SELECT * FROM a RIGHT JOIN b", "CK00001")]
    [InlineData("SELECT * FROM a RIGHT JOIN b ON", "CS00003")]
    [InlineData("SELECT * FROM (a RIGHT JOIN b USING (id)", "CS00003")]
    [InlineData("SELECT * FROM a FULL JOIN b USING (id)", "CK00001")]
    public void Parse_InvalidRightGroup_ShouldReportDiagnostic(string sql, string code)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Parenthesized joins enforce configured depth (#1525)")]
    public void Parse_DeepGroups_ShouldRefuseBeforeStackExhaustion()
    {
        // Arrange
        var parser = new SqlQueryParser(new SqlQueryParserOptions { ExpressionNestingLimit = 32 });
        string sql = "SELECT * FROM " + new string('(', 4096) + "a RIGHT JOIN b USING (id)" + new string(')', 4096);

        // Act
        var statement = parser.Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS00006");
    }
}
