using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlDerivedTableParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Derived tables: FROM comma and JOIN retain typed query nodes")]
    [InlineData("SELECT d.id FROM (SELECT id FROM t) AS d;")]
    [InlineData("SELECT * FROM t, (SELECT id FROM u) d;")]
    [InlineData("SELECT * FROM t JOIN (SELECT id FROM u) d ON t.id = d.id;")]
    [InlineData("SELECT * FROM (SELECT id FROM t) d (renamed);")]
    [InlineData("SELECT * FROM (SELECT * FROM (SELECT id FROM t) nested) d;")]
    public void Parse_DerivedRelation_ShouldRoundTrip(string sql)
    {
        // Arrange / Act
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        // Assert
        statement.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        var relations = select.From.SelectMany(item => new[] { item.Table }.Concat(item.Joins.Select(join => join.Table)));
        relations.OfType<SqlDerivedTableReference>().ShouldContain(relation => relation.Alias == "d");
        var rendered = SqlExpressionRenderer.Render(select);
        var reparsed = (SqlQueryStatement)new SqlQueryParser().Parse(rendered);
        reparsed.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        SqlExpressionRenderer.Render(reparsed.SqlExpression.ShouldBeOfType<SqlSelectExpression>()).ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - DML subqueries: WHERE accepts SELECT bodies")]
    [InlineData("UPDATE t SET id = 2 WHERE EXISTS (SELECT id FROM u);")]
    [InlineData("DELETE FROM t WHERE id IN (SELECT id FROM u);")]
    [InlineData("DELETE FROM t WHERE id > (SELECT AVG(id) FROM t);")]
    public void Parse_DmlPredicate_ShouldAcceptSubqueries(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Derived tables: missing aliases and malformed column lists fail")]
    [InlineData("SELECT * FROM (SELECT id FROM t);")]
    [InlineData("SELECT * FROM (SELECT id FROM t) AS;")]
    [InlineData("SELECT * FROM (SELECT id FROM t) d ();")]
    [InlineData("SELECT * FROM (SELECT id FROM t) d (id,);")]
    public void Parse_InvalidDerivedRelation_ShouldReportSyntaxError(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(d => d.Code == "SQL0003");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Derived tables: mutation targets remain named relations")]
    public void Parse_DerivedInsertTarget_ShouldReportCapabilityError()
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("INSERT INTO (SELECT id FROM t) d VALUES (1);");

        // Assert
        statement.Diagnostics.ShouldContain(d => d.Code == "COHDBL001");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Derived tables: reference classification waits for catalog binding")]
    [InlineData("SELECT * FROM t, (SELECT t.id FROM u) d;", "t.id")]
    [InlineData("SELECT * FROM t, (SELECT dbo.t.id FROM u) d;", "dbo.t.id")]
    [InlineData("SELECT * FROM t, (SELECT \"t\" . \"id\" FROM u) d;", "\"t\" . \"id\"")]
    [InlineData("SELECT * FROM (SELECT bogus.id FROM t) d;", "bogus.id")]
    [InlineData("SELECT * FROM (SELECT t.id FROM t hidden) d;", "t.id")]
    public void Parse_DerivedReference_ShouldPreserveSpanForBinding(string sql, string reference)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        var query = ((SqlQueryStatement)statement).SqlExpression.ShouldBeOfType<SqlSelectExpression>();
        var derived = query.From.Select(item => item.Table).OfType<SqlDerivedTableReference>().Single();
        var column = derived.Query.Columns[0].Expression.ShouldBeOfType<SqlColumnReferenceExpression>();
        sql[column.Location!.Start..column.Location.End].ShouldBe(reference);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Derived tables: bodies count toward the 32-level query limit")]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void Parse_NestedDerivedTables_ShouldEnforceDepth(int depth, bool rejected)
    {
        // Arrange
        string sql = "SELECT id FROM t";
        for (int i = 0; i < depth; i++) { sql = $"SELECT * FROM ({sql}) d"; }

        // Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.Any(d => d.Code == "COHDBL001" && d.Message!.Contains("limit of 32 levels")).ShouldBe(rejected);
    }
}
