using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlJoinShapeTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - FROM: comma retains each join tree and USING order")]
    public void Parse_CommaAndJoins_ShouldPreserveGrouping()
    {
        // Arrange
        const string sql = "SELECT * FROM a, b LEFT OUTER JOIN c USING (id, name) CROSS JOIN d;";

        // Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        // Assert
        statement.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        select.From.Count.ShouldBe(2);
        select.From[0].Table.ShouldBeOfType<SqlTableReference>().TableName.ShouldBe("a");
        select.From[0].Joins.ShouldBeEmpty();
        select.From[1].Table.ShouldBeOfType<SqlTableReference>().TableName.ShouldBe("b");
        select.From[1].Joins.Select(j => j.JoinType).ShouldBe([SqlJoinType.LeftOuter, SqlJoinType.Cross]);
        select.From[1].Joins[0].UsingColumns.ShouldBe(["id", "name"]);
        var canonical = SqlExpressionRenderer.Render(select);
        canonical.ShouldBe(sql.TrimEnd(';'));
        ((SqlQueryStatement)new SqlQueryParser().Parse(canonical)).Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - USING: preflight skips advertised clauses and locates the next excluded form")]
    [InlineData("SELECT * FROM t JOIN u USING (id) NATURAL JOIN v;", "NATURAL")]
    [InlineData("SELECT * FROM t JOIN u USING (id) WINDOW w AS ();", "WINDOW")]
    [InlineData("SELECT * FROM t JOIN u USING (id) ORDER BY ROW_NUMBER() OVER ();", "OVER")]
    [InlineData("SELECT * FROM t, LATERAL (SELECT id FROM u) d;", "LATERAL")]
    [InlineData("SELECT * FROM t JOIN LATERAL (SELECT id FROM u) d ON TRUE;", "LATERAL")]

    public void Parse_ExcludedAfterUsing_ShouldLocateFirstUnsupported(string sql, string token)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        // Assert
        var diagnostic = statement.Diagnostics.Single(d => d.Code == "CK00001");
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe(token);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - JOIN: malformed joins and USING outside SELECT fail")]
    [InlineData("SELECT * FROM t CROSS JOIN u ON TRUE;")]
    [InlineData("SELECT * FROM t CROSS JOIN u USING (id);")]
    [InlineData("SELECT * FROM t JOIN u USING ();")]
    [InlineData("SELECT * FROM t JOIN u USING (id,);")]
    [InlineData("SELECT * FROM t JOIN u USING id;")]
    [InlineData("SELECT * FROM t JOIN u USING (id) ON TRUE;")]
    [InlineData("SELECT * FROM t LEFT JOIN u;")]
    [InlineData("SELECT * FROM t INNER JOIN u;")]
    [InlineData("SELECT * FROM t,;")]
    [InlineData("SELECT * FROM t,")]
    [InlineData("DELETE FROM t USING u WHERE t.id = u.id;")]
    [InlineData("CREATE INDEX ix ON t USING btree (id);")]
    public void Parse_InvalidJoin_ShouldReportError(string sql)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(d => d.Severity == DiagnosticSeverity.Error);
    }
}
