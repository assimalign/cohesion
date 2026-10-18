using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlCteParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - WITH: supported statement prefixes parse completely")]
    [InlineData("WITH a AS (SELECT id FROM t) SELECT * FROM a;")]
    [InlineData("WITH a (x, y) AS (SELECT id, name FROM t), b AS (SELECT x FROM a) SELECT * FROM b;")]
    [InlineData("WITH a AS (SELECT t.id, COUNT(*) AS n FROM t JOIN u ON t.id = u.id WHERE EXISTS (SELECT id FROM v) GROUP BY t.id) SELECT * FROM a;")]
    [InlineData("WITH a AS (SELECT id FROM t) INSERT INTO u SELECT * FROM a;")]
    [InlineData("INSERT INTO u WITH a AS (SELECT id FROM t) SELECT * FROM a;")]
    [InlineData("WITH a AS (SELECT id FROM t) UPDATE t SET id = 2 WHERE id IN (SELECT id FROM a);")]
    [InlineData("WITH a AS (SELECT id FROM t) DELETE FROM t WHERE EXISTS (SELECT id FROM a);")]
    [InlineData("WITH a AS (SELECT id FROM t WHERE id IN (SELECT id FROM u)) SELECT (SELECT MAX(id) FROM a) FROM t;")]
    [InlineData("WITH a AS (SELECT materialized FROM t) SELECT materialized FROM a;")]
    [InlineData("WITH a AS (SELECT id AS materialized FROM t) SELECT materialized AS materialized FROM a;")]
    [InlineData("WITH recent AS (SELECT id FROM t), top AS (SELECT id FROM recent) SELECT top.id FROM top;")]
    [InlineData("WITH ties AS (SELECT id FROM t) SELECT * FROM ties;")]
    [InlineData("WITH ties (key_id) AS (SELECT id FROM t) SELECT key_id FROM ties;")]
    public void Parse_WithPrefix_ShouldAcceptSupportedForms(string sql)
    {
        // Arrange
        var parser = new SqlQueryParser();

        // Act
        var statement = parser.Parse(sql);

        // Assert
        statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - WITH: AST exposes ordered definitions and renamed outputs")]
    public void Parse_WithDefinitions_ShouldExposeNamesColumnsAndBodies()
    {
        // Arrange
        var parser = new SqlQueryParser();
        const string sql = "WITH a (x, y) AS (SELECT id, name FROM t), b AS (SELECT x FROM a) SELECT * FROM b;";

        // Act
        var statement = (SqlQueryStatement)parser.Parse(sql);
        var select = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        // Assert
        select.CommonTables.Select(item => item.Name).ShouldBe(["a", "b"]);
        select.CommonTables[0].Columns.ShouldBe(["x", "y"]);
        select.CommonTables[0].Body.Columns.Count.ShouldBe(2);
        select.CommonTables[1].Columns.ShouldBeEmpty();
        select.CommonTables[0].Location.Start.ShouldBe(5);
        select.CommonTables[0].Location.End.ShouldBe(sql.IndexOf("), b", System.StringComparison.Ordinal) + 1);
        var rendered = SqlExpressionRenderer.Render(select);
        var roundTrip = (SqlQueryStatement)new SqlQueryParser().Parse(rendered);
        roundTrip.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        SqlTreeDump.Dump(roundTrip.SqlExpression.ShouldBeOfType<SqlSelectExpression>()).ShouldBe(SqlTreeDump.Dump(select));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - WITH: excluded forms retain precise capability diagnostics")]
    [InlineData("WITH RECURSIVE a AS (SELECT * FROM t) SELECT * FROM a;", "RECURSIVE")]
    [InlineData("SELECT * FROM (WITH a AS (SELECT * FROM t) SELECT * FROM a) d;", "WITH inside")]
    [InlineData("SELECT id FROM t WHERE id IN (WITH a AS (SELECT id FROM t) SELECT id FROM a);", "WITH inside")]
    [InlineData("SELECT (WITH a AS (SELECT id FROM t) SELECT id FROM a) FROM t;", "WITH inside")]
    [InlineData("SELECT * FROM t WHERE EXISTS (WITH a AS (SELECT id FROM t) SELECT id FROM a);", "WITH inside")]
    [InlineData("WITH a AS MATERIALIZED (SELECT * FROM t) SELECT * FROM a;", "MATERIALIZED")]
    [InlineData("WITH a AS NOT MATERIALIZED (SELECT * FROM t) SELECT * FROM a;", "MATERIALIZED")]
    [InlineData("WITH a AS (INSERT INTO t VALUES (1)) SELECT * FROM a;", "INSERT")]
    [InlineData("WITH a AS (UPDATE t SET id = 1) SELECT * FROM a;", "UPDATE")]
    [InlineData("WITH a AS (DELETE FROM t) SELECT * FROM a;", "DELETE")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d;", "DELETE")]
    [InlineData("WITH a AS (SELECT id FROM t) SEARCH DEPTH FIRST BY id SET ord SELECT * FROM a;", "SEARCH/CYCLE")]
    [InlineData("WITH a AS (SELECT id FROM t) CYCLE id SET mark USING path SELECT * FROM a;", "SEARCH/CYCLE")]
    [InlineData("WITH a AS (SELECT ROW_NUMBER() OVER () AS n FROM t) SELECT * FROM a;", "OVER")]
    [InlineData("WITH a AS (SELECT * FROM t) SELECT * FROM a NATURAL JOIN u;", "NATURAL")]
    [InlineData("WITH a AS (SELECT * FROM t) SELECT * FROM a WINDOW w AS ();", "WINDOW")]
    [InlineData("WITH top AS (SELECT id FROM t) SELECT TOP (1) WITH TIES * FROM top;", "WITH TIES")]
    [InlineData("WITH natural AS (SELECT id FROM t) SELECT * FROM natural NATURAL JOIN u;", "NATURAL")]
    [InlineData("WITH window AS (SELECT id FROM t) SELECT * FROM window WINDOW w AS ();", "WINDOW")]
    public void Parse_ExcludedWithForm_ShouldNameUnsupportedConstruct(string sql, string construct)
    {
        // Arrange
        var parser = new SqlQueryParser();

        // Act
        var statement = parser.Parse(sql);

        // Assert
        var diagnostic = statement.Diagnostics.Where(item => item.Code == "COHDBL001").ShouldHaveSingleItem();
        diagnostic.Message!.ShouldContain(construct);
        statement.Diagnostics.ShouldNotContain(item => item.Code == "SQL0002");
        if (construct == "RECURSIVE")
        {
            statement.Diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error).ShouldBe(1);
            diagnostic.Start.ShouldBe(sql.IndexOf("RECURSIVE", System.StringComparison.Ordinal));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - WITH: malformed lists and unsupported command roots are syntax errors")]
    [InlineData("WITH a () AS (SELECT id FROM t) SELECT * FROM a;")]
    [InlineData("WITH a AS (SELECT id FROM t) CREATE TABLE u (id INT);")]
    [InlineData("WITH a AS (SELECT id FROM t), SELECT * FROM a;")]
    [InlineData("WITH a AS SELECT id FROM t SELECT * FROM a;")]
    public void Parse_InvalidWithSyntax_ShouldReportSyntaxError(string sql)
    {
        // Arrange
        var parser = new SqlQueryParser();

        // Act
        var statement = parser.Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(item => item.Code == "SQL0003");
    }
}
