using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

/// <summary>
/// Verifies that the SQL profile and parser expose the same implemented grammar surface.
/// </summary>
public class SqlLanguageProfileTests
{
    [Theory]
    [InlineData(SqlClauses.Select, "SELECT 1;")]
    [InlineData(SqlClauses.Insert, "INSERT INTO t (id) VALUES (1);")]
    [InlineData(SqlClauses.Update, "UPDATE t SET id = 1;")]
    [InlineData(SqlClauses.Delete, "DELETE FROM t;")]
    [InlineData(SqlClauses.CreateTable, "CREATE TABLE t (id INT);")]
    [InlineData(SqlClauses.CreateIndex, "CREATE INDEX ix_t_id ON t (id);")]
    [InlineData(SqlClauses.AlterTable, "ALTER TABLE t ADD COLUMN name TEXT;")]
    [InlineData(SqlClauses.DropTable, "DROP TABLE t;")]
    [InlineData(SqlClauses.DropIndex, "DROP INDEX ix_t_id ON t;")]
    [InlineData(SqlClauses.From, "SELECT * FROM t;")]
    [InlineData(SqlClauses.Join, "SELECT * FROM t JOIN u ON t.id = u.id;")]
    [InlineData(SqlClauses.Where, "SELECT * FROM t WHERE id = 1;")]
    [InlineData(SqlClauses.GroupBy, "SELECT id FROM t GROUP BY id;")]
    [InlineData(SqlClauses.Having, "SELECT id FROM t GROUP BY id HAVING COUNT(*) > 0;")]
    [InlineData(SqlClauses.OrderBy, "SELECT * FROM t ORDER BY id;")]
    [InlineData(SqlClauses.Limit, "SELECT * FROM t LIMIT 1;")]
    [InlineData(SqlClauses.Offset, "SELECT * FROM t LIMIT 1 OFFSET 1;")]
    [InlineData(SqlClauses.Values, "INSERT INTO t (id) VALUES (1);")]
    [InlineData(SqlClauses.Subquery, "SELECT * FROM t WHERE id IN (SELECT id FROM u);")]
    [InlineData(SqlClauses.Case, "SELECT CASE WHEN id = 1 THEN 'one' ELSE 'other' END FROM t;")]
    [InlineData(SqlClauses.Cast, "SELECT CAST(id AS INT) FROM t;")]
    public void Parse_ImplementedClause_IsDeclaredAndParses(string clause, string sql)
    {
        SqlLanguageProfile.Instance.Supports(clause).ShouldBeTrue();

        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics
            .ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(SqlClauses.Window)]
    [InlineData(SqlClauses.SetOperation)]
    [InlineData(SqlClauses.Cte)]
    [InlineData(SqlClauses.Intersect)]
    [InlineData(SqlClauses.Except)]
    [InlineData(SqlClauses.Recursive)]
    [InlineData(SqlClauses.Fetch)]
    [InlineData(SqlClauses.Over)]
    [InlineData(SqlClauses.Partition)]
    [InlineData(SqlClauses.Returning)]
    [InlineData(SqlClauses.Top)]
    [InlineData(SqlClauses.All)]
    [InlineData(SqlClauses.Natural)]
    [InlineData(SqlClauses.Using)]
    [InlineData(SqlClauses.CreateView)]
    [InlineData(SqlClauses.DropView)]
    [InlineData(SqlClauses.Constraint)]
    [InlineData(SqlClauses.ForeignKey)]
    [InlineData(SqlClauses.References)]
    [InlineData(SqlClauses.Check)]
    [InlineData(SqlClauses.UniqueConstraint)]
    [InlineData(SqlClauses.Cascade)]
    [InlineData(SqlClauses.Restrict)]
    [InlineData(SqlClauses.Begin)]
    [InlineData(SqlClauses.Commit)]
    [InlineData(SqlClauses.Rollback)]
    [InlineData(SqlClauses.Transaction)]
    public void Profile_UnsupportedClause_IsNotDeclared(string clause)
    {
        SqlLanguageProfile.Instance.Supports(clause).ShouldBeFalse();
    }

    [Theory]
    [InlineData(SqlClauses.Window, "SELECT * FROM t WINDOW w AS ();")]
    [InlineData(SqlClauses.Intersect, "SELECT * FROM t INTERSECT SELECT * FROM u;")]
    [InlineData(SqlClauses.Except, "SELECT * FROM t EXCEPT SELECT * FROM u;")]
    [InlineData(SqlClauses.Recursive, "WITH RECURSIVE cte AS (SELECT * FROM t) SELECT * FROM cte;")]
    [InlineData(SqlClauses.Fetch, "SELECT * FROM t FETCH NEXT 1 ROWS ONLY;")]
    [InlineData(SqlClauses.Over, "SELECT ROW_NUMBER() OVER () FROM t;")]
    [InlineData(SqlClauses.Partition, "SELECT ROW_NUMBER() PARTITION BY id FROM t;")]
    [InlineData(SqlClauses.SetOperation, "SELECT * FROM t UNION SELECT * FROM u;")]
    [InlineData(SqlClauses.Cte, "WITH cte AS (SELECT * FROM t) SELECT * FROM cte;")]
    [InlineData(SqlClauses.Returning, "INSERT INTO t (id) VALUES (1) RETURNING id;")]
    [InlineData(SqlClauses.Top, "SELECT TOP 1 * FROM t;")]
    [InlineData(SqlClauses.All, "SELECT ALL id FROM t;")]
    [InlineData(SqlClauses.Natural, "SELECT * FROM t NATURAL JOIN u;")]
    [InlineData(SqlClauses.Using, "SELECT * FROM t JOIN u USING (id);")]
    [InlineData(SqlClauses.CreateView, "CREATE VIEW active_users AS SELECT * FROM users;")]
    [InlineData(SqlClauses.DropView, "DROP VIEW active_users;")]
    [InlineData(SqlClauses.Constraint, "CREATE TABLE t (id INT, CONSTRAINT ck CHECK (id > 0));")]
    [InlineData(SqlClauses.ForeignKey, "CREATE TABLE t (parent_id INT FOREIGN KEY);")]
    [InlineData(SqlClauses.References, "CREATE TABLE t (parent_id INT REFERENCES parent(id));")]
    [InlineData(SqlClauses.Check, "CREATE TABLE t (id INT CHECK (id > 0));")]
    [InlineData(SqlClauses.UniqueConstraint, "CREATE TABLE t (email TEXT UNIQUE);")]
    [InlineData(SqlClauses.Cascade, "DROP TABLE t CASCADE;")]
    [InlineData(SqlClauses.Restrict, "DROP TABLE t RESTRICT;")]
    [InlineData(SqlClauses.Begin, "BEGIN;")]
    [InlineData(SqlClauses.Commit, "COMMIT;")]
    [InlineData(SqlClauses.Rollback, "ROLLBACK;")]
    [InlineData(SqlClauses.Transaction, "TRANSACTION;")]
    public void Parse_UnsupportedClause_EmitsModelSpecificDiagnostic(string clause, string sql)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "COHDBL001");
        diagnostic.Message.ShouldNotBeNull();
        diagnostic.Message!.ShouldContain(clause);
        diagnostic.Message.ShouldContain("SQL");
        statement.Diagnostics.ShouldNotContain(item => item.Code == "SQL0002");
    }

    [Fact]
    public void Parse_CreateUniqueIndex_DoesNotMisclassifyUniqueAsConstraint()
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(
            "CREATE UNIQUE /* retained trivia */ INDEX ix_t_email ON t (email);");

        statement.Diagnostics.ShouldNotContain(item => item.Code == "COHDBL001");
        statement.SqlExpression.ShouldBeOfType<SqlCreateIndexExpression>().IsUnique.ShouldBeTrue();
    }

    [Fact]
    public void Parse_UnsupportedClause_IsVisibleToAnalyzers()
    {
        var observer = new UnsupportedClauseObserver();
        var options = new QueryParserOptions();
        options.Analyzers.Add(observer);

        _ = new SqlQueryParser(options).Parse("SELECT TOP 1 * FROM t;");

        observer.SawUnsupportedClause.ShouldBeTrue();
    }

    private sealed class UnsupportedClauseObserver : QueryAnalyzer
    {
        public bool SawUnsupportedClause { get; private set; }

        public override Task AnalyzeAsync(
            QueryAnalyzerContext context,
            CancellationToken cancellationToken = default)
        {
            SawUnsupportedClause = context.Statement.Diagnostics
                .Any(item => item.Code == "COHDBL001");
            return Task.CompletedTask;
        }
    }
}
