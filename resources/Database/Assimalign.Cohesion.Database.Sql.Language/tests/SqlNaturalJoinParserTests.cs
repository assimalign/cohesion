using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlNaturalJoinParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - NATURAL: every join spelling and qualified wildcard round trips (#1792)")]
    [InlineData("JOIN")]
    [InlineData("INNER JOIN")]
    [InlineData("LEFT JOIN")]
    [InlineData("LEFT OUTER JOIN")]
    [InlineData("RIGHT JOIN")]
    [InlineData("RIGHT OUTER JOIN")]
    [InlineData("FULL JOIN")]
    [InlineData("FULL OUTER JOIN")]
    [InlineData("CROSS JOIN")]
    public void Parse_NaturalJoin_ShouldRetainShapeAndRender(string kind)
    {
        // Arrange
        string sql = "SELECT a.*, s.b.* FROM a NATURAL " + kind + " s.b";
        // Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();
        string canonical = SqlExpressionRenderer.Render(select);
        var reparsed = (SqlQueryStatement)new SqlQueryParser().Parse(canonical);
        // Assert
        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        reparsed.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        statement.ExpressionNestingDepth.ShouldBe(1);
        var join = select.From[0].Joins.ShouldHaveSingleItem();
        join.IsNatural.ShouldBeTrue();
        join.UsingColumns.ShouldBeEmpty();
        sql[join.KeywordLocation!.Start..join.KeywordLocation.End].ShouldBe("NATURAL " + kind);
        sql[join.Location!.Start..join.Location.End].ShouldBe("NATURAL " + kind + " s.b");
        select.Columns[0].Expression.ShouldBeOfType<SqlQualifiedStarExpression>().TableName.ShouldBe("a");
        select.Columns[1].Expression.ShouldBeOfType<SqlQualifiedStarExpression>().SchemaName.ShouldBe("s");
        SqlExpressionRenderer.Render(reparsed.SqlExpression.ShouldBeOfType<SqlSelectExpression>()).ShouldBe(canonical);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - NATURAL and stars: excluded positions retain CK00001 (#1792)")]
    [InlineData("SELECT COUNT(t.*) FROM t")]
    [InlineData("SELECT COUNT(s.t.*) FROM s.t")]
    [InlineData("SELECT 1 FROM t WHERE t.* IS NULL")]
    [InlineData("UPDATE t SET id=u.* FROM u")]
    [InlineData("DELETE FROM t USING u WHERE u.* IS NULL")]
    [InlineData("MERGE INTO t USING u ON u.* IS NULL WHEN MATCHED THEN DO NOTHING")]
    [InlineData("UPDATE t SET id=1 RETURNING t.*")]
    [InlineData("UPDATE t SET id=u.id FROM u RETURNING u.*")]
    [InlineData("DELETE FROM t USING u RETURNING u.*")]
    [InlineData("MERGE INTO t USING u ON t.id=u.id WHEN MATCHED THEN DO NOTHING RETURNING u.*")]
    [InlineData("DELETE FROM t RETURNING t.*")]
    [InlineData("INSERT INTO t VALUES(1) RETURNING t.*")]
    [InlineData("UPDATE t SET id=1 FROM a NATURAL LEFT JOIN b")]
    [InlineData("DELETE FROM t USING (a NATURAL JOIN b)")]
    [InlineData("MERGE INTO t USING a NATURAL JOIN b ON t.id=a.id WHEN MATCHED THEN DO NOTHING")]
    [InlineData("SELECT * FROM t JOIN LATERAL (SELECT id FROM u) d ON TRUE")]
    public void Parse_ExcludedPosition_ShouldKeepCapabilityRefusal(string sql)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        // Assert
        statement.Diagnostics.ShouldContain(item => item.Code == "CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - NATURAL: explicit conditions are syntax errors (#1792)")]
    [InlineData("SELECT * FROM a NATURAL JOIN b ON a.id=b.id")]
    [InlineData("SELECT * FROM a NATURAL LEFT JOIN b USING(id)")]
    [InlineData("SELECT * FROM a NATURAL b")]
    public void Parse_NaturalCondition_ShouldRefuseSyntax(string sql)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        // Assert
        statement.Diagnostics.ShouldContain(item => item.Code == "CS00003");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - View canonical audit: natural and lateral identifiers keep their prior quoting (#1792)")]
    [InlineData("SELECT natural, lateral FROM t", "SELECT \"natural\", \"lateral\" FROM t")]
    [InlineData("SELECT t.natural, t.lateral FROM t", "SELECT t.\"natural\", t.\"lateral\" FROM t")]
    [InlineData("SELECT \"natural\".\"lateral\" FROM t AS \"natural\"", "SELECT \"natural\".\"lateral\" FROM t AS \"natural\"")]
    [InlineData("WITH natural AS (SELECT id FROM t) SELECT * FROM natural NATURAL JOIN u", "WITH \"natural\" AS (SELECT id FROM t) SELECT * FROM \"natural\" NATURAL INNER JOIN u")]
    public void Render_ContextualIdentifier_ShouldKeepCanonicalSpelling(string sql, string expected)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        // Assert
        statement.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        SqlExpressionRenderer.Render(statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>()).ShouldBe(expected);
    }
}
