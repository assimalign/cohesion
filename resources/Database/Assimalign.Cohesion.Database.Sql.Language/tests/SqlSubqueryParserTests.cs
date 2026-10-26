using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Verifies the exact executable subquery syntax, scope, and nesting boundaries.
/// </summary>
public sealed class SqlSubqueryParserTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Subquery: Accepts independent local scopes")]
    [InlineData("SELECT (SELECT u.id FROM u) FROM t;")]
    [InlineData("SELECT (SELECT t.id FROM t) FROM t;")]
    [InlineData("SELECT (SELECT local.id FROM u AS local) FROM t AS outer_row;")]
    [InlineData("SELECT (SELECT dbo.u.id FROM dbo.u) FROM t;")]
    [InlineData("SELECT (SELECT dbo.u.id FROM u) FROM t;")]
    [InlineData("SELECT id, (SELECT MAX(age) FROM t) AS oldest FROM t WHERE id IN (SELECT id FROM t WHERE age > 40) AND EXISTS (SELECT id FROM t WHERE age = 36) ORDER BY id;")]
    [InlineData("SELECT id FROM t WHERE id = (SELECT MAX(id) FROM u);")]
    [InlineData("SELECT id FROM t ORDER BY (SELECT MAX(id) FROM u) LIMIT 2;")]
    [InlineData("SELECT COUNT(*) FROM t HAVING COUNT(*) > (SELECT COUNT(*) FROM u);")]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT u.id FROM u JOIN v ON u.id = v.id GROUP BY u.id HAVING COUNT(*) > 0 ORDER BY u.id LIMIT 1);")]
    [InlineData("INSERT INTO t SELECT id FROM u WHERE id IN (SELECT id FROM v);")]
    [InlineData("WITH q AS (SELECT id FROM u) SELECT id FROM q;")]
    [InlineData("SELECT 'ANY (SELECT), LATERAL, SOME (SELECT)' FROM t;")]
    [InlineData("SELECT \"ANY\", \"LATERAL\" FROM t;")]
    [InlineData("SELECT id, lateral FROM t;")]
    public void Parse_UncorrelatedSubquery_ReportsNoError(string sql)
    {
        var statement = new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Subquery: Accepts qualified and unqualified outer references")]
    [InlineData("SELECT (SELECT outer_row.id FROM u) FROM t outer_row;", "outer_row.id")]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT u.id FROM u WHERE u.id = t.id);", "t.id")]
    [InlineData("SELECT id FROM t WHERE id IN (SELECT t.id FROM u);", "t.id")]
    [InlineData("SELECT (SELECT (SELECT t.id FROM v) FROM u) FROM t;", "t.id")]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u GROUP BY u.id HAVING u.id = t.id);", "t.id")]
    [InlineData("SELECT (SELECT dbo.t.id FROM dbo.u) FROM dbo.t;", "dbo.t.id")]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT inner_id FROM u WHERE inner_id = outer_id);", "outer_id")]
    [InlineData("SELECT t.id FROM t JOIN u ON EXISTS (SELECT v.id FROM v WHERE v.id = t.id);", "t.id")]
    [InlineData("SELECT t.id FROM t GROUP BY t.id HAVING EXISTS (SELECT u.id FROM u WHERE u.id = t.id);", "t.id")]
    [InlineData("UPDATE t SET id = 2 WHERE EXISTS (SELECT u.id FROM u WHERE u.id = t.id);", "t.id")]
    [InlineData("DELETE FROM t WHERE id IN (SELECT u.id FROM u WHERE u.id = t.id);", "t.id")]
    public void Parse_CorrelatedSubquery_ReportsNoError(string sql, string reference)
    {
        // Arrange
        var parser = new SqlQueryParser();

        // Act
        var statement = parser.Parse(sql);

        // Assert
        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
        References(statement.ShouldBeOfType<SqlQueryStatement>().SqlExpression, false)
            .Select(column => string.Join(".", new[] { column.SchemaName, column.TableAlias, column.ColumnName }.Where(part => part is not null)))
            .ShouldContain(reference);

        static IEnumerable<SqlColumnReferenceExpression> References(SqlQueryExpression query, bool child)
        {
            IEnumerable<SqlExpression?> roots = query switch
            {
                SqlSelectExpression select => select.Columns.Select(column => column.Expression)
                    .Concat(select.From.SelectMany(item => item.Joins).Select(join => join.Condition))
                    .Concat(select.GroupBy).Concat(select.OrderBy.Select(order => order.Expression))
                    .Concat(new[] { select.Where, select.Having, select.Limit, select.Offset }),
                SqlUpdateExpression update => new[] { update.Where },
                SqlDeleteExpression delete => new[] { delete.Where },
                _ => [],
            };
            return roots.OfType<SqlExpression>().SelectMany(root => Columns(root, child));
        }

        static IEnumerable<SqlColumnReferenceExpression> Columns(SqlExpression expression, bool child)
        {
            if (expression is SqlColumnReferenceExpression column && child) { yield return column; }
            var query = expression switch
            {
                SqlSubqueryExpression scalar => scalar.Select,
                SqlExistsExpression exists => exists.Subquery,
                SqlInExpression member => member.Subquery,
                _ => null,
            };
            if (query is not null)
            {
                foreach (var reference in References(query, true)) { yield return reference; }
            }
            IEnumerable<SqlExpression> operands = expression switch
            {
                SqlBinaryExpression binary => new[] { binary.Left, binary.Right },
                SqlInExpression member => new[] { member.Operand },
                _ => [],
            };
            foreach (var operand in operands)
            {
                foreach (var reference in Columns(operand, child)) { yield return reference; }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Subquery: Excludes unsupported forms")]
    [InlineData("SELECT * FROM LATERAL (SELECT id FROM u) q;", "LATERAL")]


    [InlineData("INSERT INTO t VALUES ((SELECT id FROM u));", "SUBQUERY in INSERT VALUES")]
    [InlineData("SELECT id FROM t LIMIT (SELECT id FROM u);", "LIMIT")]
    [InlineData("SELECT id FROM t OFFSET (SELECT id FROM u);", "OFFSET")]
    [InlineData("CREATE TABLE t (id INT CHECK (EXISTS (SELECT id FROM u)));", "SUBQUERY in CREATE")]
    public void Parse_UnsupportedForm_ReportsModelDiagnostic(string sql, string form)
    {
        var statement = new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldContain(item => item.Code == "CK00001" &&
            item.Message != null && item.Message.Contains(form));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Subquery: Enforces a bounded nesting depth")]
    [InlineData(1, false)]
    [InlineData(32, false)]
    [InlineData(33, true)]
    [InlineData(2000, true)]
    public void Parse_NestedSubqueries_EnforcesDepthBeforeRecursing(int depth, bool rejected)
    {
        string sql = string.Concat(Enumerable.Repeat("SELECT (", depth)) +
            "SELECT 1" + new string(')', depth) + ";";
        var parser = new SqlQueryParser();

        var statement = parser.Parse(sql);

        statement.Diagnostics.Any(item => item.Code == "CK00001" &&
            item.Message!.Contains("limit of 32 levels")).ShouldBe(rejected);
        parser.Parse("SELECT (SELECT 1);").Diagnostics.ShouldNotContain(item =>
            item.Severity == DiagnosticSeverity.Error);
    }
}
