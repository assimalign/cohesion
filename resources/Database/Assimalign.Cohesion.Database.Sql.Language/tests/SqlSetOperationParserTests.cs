using System;
using System.Linq;
using System.Threading;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlSetOperationParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Parenthesized queries: value continuations retain expression grammar")]
    [InlineData("SELECT ((SELECT a FROM t) + 1) FROM u")]
    [InlineData("SELECT a FROM t WHERE a IN ((SELECT a FROM u), 2)")]
    [InlineData("SELECT a FROM t WHERE ((SELECT COUNT(*) FROM u) > 0)")]
    [InlineData("SELECT (((SELECT a FROM t) + 1)) FROM u")]
    public void Parse_ParenthesizedQueryValue_ShouldRoundTrip(string sql)
    {
        // Arrange / Act
        var query = Parse(sql);
        var reloaded = Parse(SqlExpressionRenderer.Render(query));

        // Assert
        SqlTreeDump.Dump(reloaded).ShouldBe(SqlTreeDump.Dump(query));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Refused query constructs: one diagnostic leaves caller parentheses intact")]
    [InlineData("SELECT a FROM t WHERE a IN (SELECT a FROM u UNION CORRESPONDING SELECT a FROM v)", "CORRESPONDING")]
    [InlineData("SELECT a FROM t WHERE a IN ((WITH q AS (SELECT a FROM u) SELECT a FROM q))", "WITH inside a subquery")]
    [InlineData("SELECT a FROM t UNION (WITH q AS (SELECT a FROM u) SELECT a FROM q)", "WITH inside a subquery")]
    public void Parse_RefusedNestedQuery_ShouldReportOneDiagnostic(string sql, string construct)
    {
        // Arrange / Act
        var diagnostics = new SqlQueryParser().Parse(sql).Diagnostics;

        // Assert
        var error = diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        error.Code.ShouldBe("COHDBL001");
        error.Message!.ShouldContain(construct, Case.Sensitive);
    }
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Set operations: every quantifier and query position parses")]
    [InlineData("UNION")]
    [InlineData("UNION ALL")]
    [InlineData("UNION DISTINCT")]
    [InlineData("INTERSECT")]
    [InlineData("INTERSECT ALL")]
    [InlineData("INTERSECT DISTINCT")]
    [InlineData("EXCEPT")]
    [InlineData("EXCEPT ALL")]
    [InlineData("EXCEPT DISTINCT")]
    public void Parse_SetOperations_ShouldAcceptEveryQueryPosition(string operation)
    {
        // Arrange
        string body = $"SELECT a FROM t {operation} SELECT b FROM u";
        string[] queries = [body, $"({body})", $"SELECT id FROM x WHERE id IN ({body})",
            $"SELECT id FROM x WHERE EXISTS ({body})", $"SELECT ({body}) FROM x",
            $"INSERT INTO x {body}", $"INSERT INTO x ({body})", $"INSERT INTO x (id) ({body})",
            $"SELECT * FROM ({body}) d",
            $"WITH q AS ({body}) SELECT * FROM q", $"WITH q AS (SELECT a FROM t) {body}",
            $"INSERT INTO x WITH q AS (SELECT a FROM t) {body}", $"EXPLAIN {body}",
            $"EXPLAIN WITH q AS (SELECT a FROM t) {body}"];

        // Act / Assert
        foreach (string sql in queries)
        {
            new SqlQueryParser().Parse(sql).Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error, sql);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Set operations: precedence and combined tails retain tree shape")]
    public void Parse_PrecedenceAndTail_ShouldOwnCombinedWindow()
    {
        // Arrange / Act
        var query = Parse("SELECT a FROM t EXCEPT SELECT a FROM u UNION SELECT a FROM v INTERSECT SELECT a FROM w ORDER BY a LIMIT 2 OFFSET 1")
            .ShouldBeOfType<SqlSetOperationExpression>();

        // Assert
        query.Operator.ShouldBe(SqlSetOperator.Union);
        query.Left.ShouldBeOfType<SqlSetOperationExpression>().Operator.ShouldBe(SqlSetOperator.Except);
        query.Right.ShouldBeOfType<SqlSetOperationExpression>().Operator.ShouldBe(SqlSetOperator.Intersect);
        query.Left.OrderBy.ShouldBeEmpty();
        query.Right.Limit.ShouldBeNull();
        query.OrderBy.ShouldHaveSingleItem();
        query.Limit.ShouldNotBeNull();
        query.Offset.ShouldNotBeNull();
        Parse("SELECT a FROM t UNION SELECT TOP 1 a FROM u OFFSET 1").ShouldBeOfType<SqlSetOperationExpression>()
            .Right.LimitSyntaxKind.ShouldBe(SqlLimitSyntaxKind.Top);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Set operations: malformed operands and unsupported suffixes retain codes")]
    [InlineData("SELECT a FROM t UNION;", "SQL0003")]
    [InlineData("SELECT a FROM t ORDER BY a UNION SELECT a FROM u", "SQL0003")]
    [InlineData("SELECT a FROM t LIMIT 1 UNION SELECT a FROM u", "SQL0003")]
    [InlineData("SELECT a FROM t OFFSET 1 UNION SELECT a FROM u", "SQL0003")]
    [InlineData("SELECT a FROM t UNION CORRESPONDING SELECT a FROM u", "COHDBL001")]
    [InlineData("SELECT a FROM t EXCEPT ALL CORRESPONDING SELECT a FROM u", "COHDBL001")]
    [InlineData("SELECT id FROM t UNION SELECT id FROM u WINDOW w AS ();", "COHDBL001")]
    [InlineData("SELECT a FROM t WHERE a = ALL (SELECT a FROM u UNION SELECT a FROM v)", "COHDBL001")]
    [InlineData("SELECT a FROM t UNION SELECT a FROM u ORDER BY a NULLS LAST", "COHDBL001")]
    public void Parse_RejectedSetForm_ShouldReportCode(string sql, string code)
    {
        // Arrange / Act
        var diagnostics = new SqlQueryParser().Parse(sql).Diagnostics;

        // Assert
        diagnostics.ShouldContain(item => item.Code == code);
        if (sql.Contains("CORRESPONDING")) { diagnostics.Count(item => item.Code == code).ShouldBe(1); }
        if (sql.Contains("WINDOW")) { diagnostics.Single(item => item.Code == code).Message!.ShouldContain("WINDOW"); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Set operations: canonical text preserves associativity and operand windows")]
    [InlineData("SELECT a FROM t UNION ALL (SELECT a FROM u UNION SELECT a FROM v)")]
    [InlineData("(SELECT a FROM t UNION SELECT a FROM u) INTERSECT SELECT a FROM v")]
    [InlineData("(SELECT a FROM t ORDER BY a LIMIT 1) UNION ALL (SELECT a FROM u ORDER BY a LIMIT 1) ORDER BY 1")]
    [InlineData("WITH q AS (SELECT 1 AS a UNION ALL SELECT 2) SELECT a FROM q EXCEPT SELECT a FROM t OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY")]
    public void Render_SetTree_ShouldRoundTrip(string sql)
    {
        // Arrange
        var query = Parse(sql);

        // Act
        string canonical = SqlExpressionRenderer.Render(query);
        var reparsed = Parse(canonical);

        // Assert
        SqlTreeDump.Dump(reparsed).ShouldBe(SqlTreeDump.Dump(query));
        SqlExpressionRenderer.Render(reparsed).ShouldBe(canonical);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Set operations: nested query parentheses preserve membership")]
    public void Parse_DoubleQueryParentheses_ShouldProduceMembership()
    {
        // Arrange / Act
        var query = Parse("SELECT a IN ((SELECT a FROM t)) FROM u").ShouldBeOfType<SqlSelectExpression>();

        // Assert
        query.Columns[0].Expression.ShouldBeOfType<SqlInExpression>().Subquery.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Set operations: long chains obey the expression depth ceiling")]
    public void Parse_DeepSetChain_ShouldRejectBeforeWalking()
    {
        // Arrange
        string sql = string.Join(" UNION ALL ", Enumerable.Repeat("SELECT a FROM t", 300));

        // Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(item => item.Code == "SQL0006");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Set operations: expected query bodies keep bounded parenthesis diagnostics")]
    [InlineData("INSERT INTO t {0}")]
    [InlineData("EXPLAIN {0}")]
    [InlineData("WITH q AS ({0}) SELECT * FROM q")]
    [InlineData("SELECT * FROM ({0}) d")]
    [InlineData("SELECT a FROM t WHERE EXISTS ({0})")]
    public void Parse_DeepExpectedQuery_ShouldUseParenthesisGuard(string template)
    {
        // Arrange
        string body = new string('(', 300) + "SELECT a FROM t UNION SELECT a FROM u" + new string(')', 300);
        SqlQueryStatement? parsed = null;
        var thread = new Thread(() => parsed = new SqlQueryParser().Parse(string.Format(template, body)).ShouldBeOfType<SqlQueryStatement>(), 8 * 1024 * 1024);

        // Act
        thread.Start();
        thread.Join();

        // Assert
        parsed.ShouldNotBeNull().Diagnostics.ShouldContain(item => item.Code == "SQL0006");
    }

    private static SqlQueryBodyExpression Parse(string sql)
    {
        var statement = new SqlQueryParser().Parse(sql).ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error, sql);
        return statement.SqlExpression.ShouldBeAssignableTo<SqlQueryBodyExpression>();
    }
}
