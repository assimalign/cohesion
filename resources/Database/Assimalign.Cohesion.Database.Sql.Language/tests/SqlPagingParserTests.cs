using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlPagingParserTests
{
    public static IEnumerable<object[]> Forms()
    {
        (string Sql, SqlLimitSyntaxKind? Kind)[] forms =
        [
            ("SELECT id FROM t ORDER BY id OFFSET 2 ROWS FETCH NEXT 3 ROWS ONLY", SqlLimitSyntaxKind.Fetch),
            ("SELECT id FROM t OFFSET 1 ROW", null),
            ("SELECT id FROM t FETCH FIRST ROW ONLY", SqlLimitSyntaxKind.Fetch),
            ("SELECT id FROM t FETCH FIRST 0 ROWS ONLY", SqlLimitSyntaxKind.Fetch),
            ("SELECT id FROM t OFFSET 1 LIMIT 2", SqlLimitSyntaxKind.Limit),
            ("SELECT id FROM t LIMIT 2 OFFSET 1", SqlLimitSyntaxKind.Limit),
            ("SELECT id FROM t FETCH NEXT 2 ROWS ONLY OFFSET 1 ROW", SqlLimitSyntaxKind.Fetch),
            ("SELECT id FROM t FETCH FIRST $1 ROWS ONLY", SqlLimitSyntaxKind.Fetch),
            ("SELECT id FROM t FETCH FIRST (1 + 1) ROWS ONLY", SqlLimitSyntaxKind.Fetch),
            ("SELECT id FROM t FETCH FIRST ABS(-2) ROWS ONLY", SqlLimitSyntaxKind.Fetch),
            ("SELECT id FROM t FETCH FIRST CASE WHEN id > 0 THEN 2 ELSE 1 END ROWS ONLY", SqlLimitSyntaxKind.Fetch),
            ("SELECT id FROM t FETCH FIRST +2 ROWS ONLY", SqlLimitSyntaxKind.Fetch),
            ("SELECT id FROM t FETCH FIRST -1 ROWS ONLY", SqlLimitSyntaxKind.Fetch),
            ("SELECT id FROM t FETCH FIRST -1.5 ROWS ONLY", SqlLimitSyntaxKind.Fetch),
            ("SELECT TOP (2) id FROM t ORDER BY id", SqlLimitSyntaxKind.Top),
            ("SELECT TOP 2 * FROM t", SqlLimitSyntaxKind.Top),
            ("SELECT DISTINCT TOP (@n) name FROM t", SqlLimitSyntaxKind.Top),
            ("SELECT TOP (1 + 1) id FROM t", SqlLimitSyntaxKind.Top),
            ("SELECT first, percent, ties FROM t FETCH FIRST ROW ONLY", SqlLimitSyntaxKind.Fetch),
        ];
        foreach (var (sql, kind) in forms)
        {
            yield return [sql, kind!];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Paging: spellings retain their syntax kind and render to a fixed point")]
    [MemberData(nameof(Forms))]
    public void Parse_PagingForm_ShouldRoundTripItsSyntax(string sql, SqlLimitSyntaxKind? kind)
    {
        // Arrange / Act
        var select = Parse(sql).SqlExpression.ShouldBeOfType<SqlSelectExpression>();
        var rendered = SqlExpressionRenderer.Render(select);
        var reparsed = Parse(rendered).SqlExpression.ShouldBeOfType<SqlSelectExpression>();

        // Assert
        select.LimitSyntaxKind.ShouldBe(kind);
        SqlTreeDump.Dump(reparsed).ShouldBe(SqlTreeDump.Dump(select));
        SqlExpressionRenderer.Render(reparsed).ShouldBe(rendered);
        SqlLanguageProfile.Instance.Keywords.ToArray().ShouldNotContain("FIRST");
        if (sql.Contains("FETCH FIRST ROW ONLY"))
        {
            select.Limit.ShouldBeOfType<SqlLiteralExpression>().Value.ShouldBe("1");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Paging: every spelling parses in SELECT child positions")]
    [MemberData(nameof(Forms))]
    public void Parse_PagingInChildPositions_ShouldRetainSyntax(string sql, SqlLimitSyntaxKind? kind)
    {
        // Arrange
        string[] statements =
        [
            $"SELECT id FROM u WHERE id IN ({sql})",
            $"SELECT id FROM u WHERE EXISTS ({sql})",
            $"SELECT ({sql}) FROM u",
            $"INSERT INTO u {sql}",
            $"SELECT * FROM ({sql}) d",
            $"WITH c AS ({sql}) SELECT * FROM c",
        ];

        // Act / Assert
        foreach (string statement in statements)
        {
            var parsed = Parse(statement);
            SqlQueryBodyExpression child = parsed.SqlExpression switch
            {
                SqlInsertExpression insert => insert.SelectSource!,
                SqlSelectExpression { CommonTables.Count: > 0 } select => select.CommonTables[0].Body,
                SqlSelectExpression { Where: SqlInExpression membership } => membership.Subquery!,
                SqlSelectExpression { Where: SqlExistsExpression exists } => exists.Subquery,
                SqlSelectExpression { Columns: [{ Expression: SqlSubqueryExpression scalar }] } => scalar.Select,
                SqlSelectExpression select => select.From[0].Table.ShouldBeOfType<SqlDerivedTableReference>().Query,
                _ => throw new System.InvalidOperationException(),
            };
            child.LimitSyntaxKind.ShouldBe(kind, statement);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Paging: malformed and mixed counts receive CS00003")]
    [InlineData("SELECT id FROM t LIMIT 1 FETCH FIRST 1 ROW ONLY")]
    [InlineData("SELECT TOP (1) id FROM t LIMIT 1")]
    [InlineData("SELECT TOP (1) id FROM t OFFSET 1 ROWS")]
    [InlineData("SELECT TOP (1) id FROM t FETCH FIRST 1 ROW ONLY")]
    [InlineData("SELECT id FROM t OFFSET 1 OFFSET 2")]
    [InlineData("SELECT id FROM t LIMIT 1 LIMIT 2")]
    [InlineData("SELECT id FROM t FETCH FIRST 1 ROW ONLY FETCH NEXT 2 ROWS ONLY")]
    [InlineData("SELECT id FROM t FETCH FIRST 1 ROW")]
    [InlineData("SELECT id FROM t FETCH 1 ROW ONLY")]
    [InlineData("SELECT id FROM t FETCH FIRST 1 ONLY")]
    [InlineData("SELECT id FROM t FETCH FIRST 1+1 ROWS ONLY")]
    [InlineData("SELECT id FROM t FETCH FIRST 2*2 ROWS ONLY")]
    [InlineData("SELECT id FROM t FETCH FIRST -1+1 ROWS ONLY")]
    [InlineData("SELECT id FROM t FETCH FIRST -id ROWS ONLY")]
    [InlineData("SELECT id FROM t FETCH FIRST +@n ROWS ONLY")]
    [InlineData("SELECT id FROM t FETCH FIRST -(1 + 1) ROWS ONLY")]
    [InlineData("SELECT id FROM t FETCH FIRST 1 COLLATE binary ROWS ONLY")]
    [InlineData("SELECT id FROM t FETCH FIRST -1 COLLATE binary ROWS ONLY")]
    [InlineData("SELECT TOP id FROM t")]
    [InlineData("SELECT TOP -1 id FROM t")]
    [InlineData("SELECT TOP 1.5 id FROM t")]
    public void Parse_InvalidPaging_ShouldReportSyntax(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(item => item.Code == "CS00003");
        statement.Diagnostics.ShouldNotContain(item => item.Code == "CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Paging: exclusions name the paging form and retain first-match scanning")]
    [InlineData("SELECT * FROM t FETCH FIRST 10 PERCENT ROWS ONLY", "PERCENT")]
    [InlineData("SELECT * FROM t ORDER BY id FETCH FIRST 1 ROW WITH TIES", "WITH TIES")]
    [InlineData("SELECT TOP (10) PERCENT * FROM t", "PERCENT")]
    [InlineData("SELECT TOP (1) WITH TIES * FROM t ORDER BY id", "WITH TIES")]
    [InlineData("DELETE TOP (1) FROM t", "DML TOP")]
    [InlineData("UPDATE TOP (1) t SET id = 2", "DML TOP")]
    [InlineData("INSERT TOP (1) INTO u SELECT id FROM t", "DML TOP")]
    [InlineData("INSERT TOP (1) u SELECT id FROM t", "DML TOP")]
    [InlineData("SELECT * FROM t FETCH FIRST (SELECT id FROM t) ROWS ONLY", "FETCH")]
    [InlineData("SELECT * FROM t FETCH FIRST (1 + (SELECT id FROM t)) ROWS ONLY", "FETCH")]
    [InlineData("SELECT TOP (SELECT id FROM t) * FROM t", "TOP")]
    [InlineData("SELECT TOP 1 id FROM t WINDOW w AS (ROWS CURRENT ROW)", "ROWS")]
    [InlineData("SELECT * FROM t WINDOW w AS (ROWS CURRENT ROW) FETCH FIRST ROW ONLY", "ROWS")]
    [InlineData("SELECT SUM(id) OVER (ORDER BY id ROWS UNBOUNDED PRECEDING) FROM t", "ROWS")]
    [InlineData("SELECT SUM(id) OVER (ORDER BY id GROUPS UNBOUNDED PRECEDING) FROM t", "GROUPS")]
    public void Parse_ExcludedPaging_ShouldNameTheConstruct(string sql, string construct)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        var diagnostic = statement.Diagnostics.Where(item => item.Code == "CK00001").ShouldHaveSingleItem();
        diagnostic.Message!.ShouldContain(construct, Case.Sensitive);
        diagnostic.Message!.ShouldNotContain("CTE");
    }

    private static SqlQueryStatement Parse(string sql)
    {
        var statement = new SqlQueryParser().Parse(sql).ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error, sql);
        return statement;
    }
}
