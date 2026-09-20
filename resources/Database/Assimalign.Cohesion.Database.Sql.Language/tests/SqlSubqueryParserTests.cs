using System.Linq;

using Assimalign.Cohesion.Database.Language;

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
    [InlineData("SELECT 'ANY (SELECT), LATERAL, SOME (SELECT)' FROM t;")]
    [InlineData("SELECT \"ANY\", \"LATERAL\" FROM t;")]
    [InlineData("SELECT id, lateral FROM t;")]
    public void Parse_UncorrelatedSubquery_ReportsNoError(string sql)
    {
        var statement = new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Subquery: Rejects outer qualifiers explicitly")]
    [InlineData("SELECT (SELECT outer_row.id FROM u) FROM t outer_row;", "outer_row.id")]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT u.id FROM u WHERE u.id = t.id);", "t.id")]
    [InlineData("SELECT id FROM t WHERE id IN (SELECT t.id FROM u);", "t.id")]
    [InlineData("SELECT (SELECT (SELECT t.id FROM v) FROM u) FROM t;", "t.id")]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT 1 FROM u GROUP BY u.id HAVING u.id = t.id);", "t.id")]
    [InlineData("SELECT (SELECT dbo.t.id FROM dbo.u) FROM dbo.t;", "dbo.t.id")]
    public void Parse_CorrelatedSubquery_ReportsPreciseDiagnostic(string sql, string reference)
    {
        var statement = new SqlQueryParser().Parse(sql);

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "COHDBL001");
        diagnostic.Message!.ShouldContain("Correlated SQL subqueries are not supported");
        diagnostic.Message!.ShouldContain(reference);
        diagnostic.Message!.ShouldContain("local FROM/JOIN scope");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Subquery: Excludes unsupported forms")]
    [InlineData("SELECT id FROM t WHERE id = ANY (SELECT id FROM u);", "ANY quantified comparison")]
    [InlineData("SELECT id FROM t WHERE id >= ALL (SELECT id FROM u);", "ALL quantified comparison")]
    [InlineData("SELECT id FROM t WHERE id <> SOME (SELECT id FROM u);", "SOME quantified comparison")]
    [InlineData("SELECT id FROM t WHERE id = any /* trivia */ (SELECT id FROM u);", "ANY quantified comparison")]
    [InlineData("SELECT * FROM LATERAL (SELECT id FROM u) q;", "LATERAL")]
    [InlineData("SELECT * FROM t JOIN (SELECT id FROM u) q ON t.id = q.id;", "derived-table")]
    [InlineData("WITH q AS (SELECT id FROM u) SELECT id FROM q;", "WITH")]
    [InlineData("UPDATE t SET id = 2 WHERE EXISTS (SELECT id FROM u);", "SUBQUERY in UPDATE")]
    [InlineData("INSERT INTO t VALUES ((SELECT id FROM u));", "SUBQUERY in INSERT VALUES")]
    [InlineData("SELECT id FROM t LIMIT (SELECT id FROM u);", "LIMIT or OFFSET")]
    [InlineData("SELECT id FROM t OFFSET (SELECT id FROM u);", "LIMIT or OFFSET")]
    [InlineData("CREATE TABLE t (id INT CHECK (EXISTS (SELECT id FROM u)));", "SUBQUERY in CREATE")]
    public void Parse_UnsupportedForm_ReportsModelDiagnostic(string sql, string form)
    {
        var statement = new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldContain(item => item.Code == "COHDBL001" &&
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

        statement.Diagnostics.Any(item => item.Code == "COHDBL001" &&
            item.Message!.Contains("limit of 32 levels")).ShouldBe(rejected);
        parser.Parse("SELECT (SELECT 1);").Diagnostics.ShouldNotContain(item =>
            item.Severity == DiagnosticSeverity.Error);
    }
}
