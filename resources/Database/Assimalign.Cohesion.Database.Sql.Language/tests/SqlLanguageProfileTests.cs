using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

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
    [InlineData(SqlClauses.JsonValue, "SELECT JSON_VALUE(doc,'$.a' RETURNING INT) FROM t;")]
    [InlineData(SqlClauses.JsonQuery, "SELECT JSON_QUERY(doc,'$[*]' WITH WRAPPER) FROM t;")]
    [InlineData(SqlClauses.JsonExists, "SELECT JSON_EXISTS(doc,'$.a') FROM t;")]
    [InlineData(SqlClauses.SetOperation, "SELECT a FROM t UNION SELECT a FROM u;")]
    [InlineData(SqlClauses.Intersect, "SELECT a FROM t INTERSECT SELECT a FROM u;")]
    [InlineData(SqlClauses.Except, "SELECT a FROM t EXCEPT SELECT a FROM u;")]
    [InlineData(SqlClauses.Explain, "EXPLAIN SELECT * FROM t;")]
    [InlineData(SqlClauses.Insert, "INSERT INTO t (id) VALUES (1);")]
    [InlineData(SqlClauses.Merge, "MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN DELETE;")]
    [InlineData(SqlClauses.OnConflict, "INSERT INTO t (id) VALUES (1) ON CONFLICT DO NOTHING;")]
    [InlineData(SqlClauses.Returning, "INSERT INTO t (id) VALUES (1) RETURNING id;")]
    [InlineData(SqlClauses.Update, "UPDATE t SET id = 1;")]
    [InlineData(SqlClauses.UpdateFrom, "UPDATE t SET id=s.id FROM s WHERE t.id=s.id;")]
    [InlineData(SqlClauses.DeleteUsing, "DELETE FROM t USING s WHERE t.id=s.id;")]
    [InlineData(SqlClauses.Delete, "DELETE FROM t;")]
    [InlineData(SqlClauses.AlterIdentity, "ALTER TABLE t ALTER id RESTART;")]
    [InlineData(SqlClauses.Serial, "CREATE TABLE t(id SERIAL);")]
    [InlineData(SqlClauses.Overriding, "INSERT INTO t OVERRIDING USER VALUE VALUES(9);")]
    [InlineData(SqlClauses.OwnedBy, "CREATE SEQUENCE s OWNED BY t.id;")]
    [InlineData(SqlClauses.CreateSequence, "CREATE SEQUENCE s;")]
    [InlineData(SqlClauses.AlterSequence, "ALTER SEQUENCE s RESTART;")]
    [InlineData(SqlClauses.DropSequence, "DROP SEQUENCE s;")]
    [InlineData(SqlClauses.NextValue, "SELECT NEXT VALUE FOR s;")]
    [InlineData(SqlClauses.Identity, "CREATE TABLE t (id INT IDENTITY);")]
    [InlineData(SqlClauses.CreateTable, "CREATE TABLE t (id INT);")]
    [InlineData(SqlClauses.CreateTableAs, "CREATE TABLE t (id) AS SELECT 1 WITH NO DATA;")]
    [InlineData(SqlClauses.IndexKeyOrder, "CREATE INDEX ix ON t(id DESC NULLS LAST);")]
    [InlineData(SqlClauses.PartialIndex, "CREATE INDEX ix ON t (id) WHERE id > 0;")]
    [InlineData(SqlClauses.CreateIndex, "CREATE INDEX ix_t_id ON t (id);")]
    [InlineData(SqlClauses.AlterTable, "ALTER TABLE t ADD COLUMN name TEXT;")]
    [InlineData(SqlClauses.DropTable, "DROP TABLE t;")]
    [InlineData(SqlClauses.Truncate, "TRUNCATE TABLE ONLY t RESTART IDENTITY CASCADE;")]
    [InlineData(SqlClauses.CopyTo, "COPY t TO STDOUT WITH (FORMAT csv, HEADER);")]
    [InlineData(SqlClauses.CreateView, "CREATE VIEW v AS SELECT a FROM t;")]
    [InlineData(SqlClauses.DropView, "DROP VIEW v;")]
    [InlineData(SqlClauses.DropIndex, "DROP INDEX ix_t_id ON t;")]
    [InlineData(SqlClauses.From, "SELECT * FROM t;")]
    [InlineData(SqlClauses.Join, "SELECT * FROM t JOIN u ON t.id = u.id;")]
    [InlineData(SqlClauses.Where, "SELECT * FROM t WHERE id = 1;")]
    [InlineData(SqlClauses.Filter, "SELECT COUNT(*) FILTER (WHERE a > 0) FROM t;")]
    [InlineData(SqlClauses.GroupingSets, "SELECT a FROM t GROUP BY GROUPING SETS ((a), ());")]
    [InlineData(SqlClauses.Rollup, "SELECT a FROM t GROUP BY ROLLUP(a);")]
    [InlineData(SqlClauses.Cube, "SELECT a FROM t GROUP BY CUBE(a);")]
    [InlineData(SqlClauses.GroupBy, "SELECT id FROM t GROUP BY id;")]
    [InlineData(SqlClauses.Window, "SELECT RANK() OVER w WINDOW w AS();")]
    [InlineData(SqlClauses.WindowRows, "SELECT SUM(v) OVER(ROWS 2 PRECEDING) FROM t;")]
    [InlineData(SqlClauses.WindowGroups, "SELECT SUM(v) OVER(ORDER BY k GROUPS 1 PRECEDING) FROM t;")]
    [InlineData(SqlClauses.WindowExclude, "SELECT SUM(v) OVER(ROWS CURRENT ROW EXCLUDE TIES) FROM t;")]
    [InlineData(SqlClauses.WindowNullTreatment, "SELECT LAG(v) IGNORE NULLS OVER() FROM t;")]
    [InlineData(SqlClauses.WindowRange, "SELECT SUM(v) OVER(RANGE UNBOUNDED PRECEDING) FROM t;")]
    [InlineData(SqlClauses.Over, "SELECT RANK() OVER();")]
    [InlineData(SqlClauses.Partition, "SELECT RANK() OVER(PARTITION BY a) FROM t;")]
    [InlineData(SqlClauses.Having, "SELECT id FROM t GROUP BY id HAVING COUNT(*) > 0;")]
    [InlineData(SqlClauses.OrderBy, "SELECT * FROM t ORDER BY id;")]
    [InlineData(SqlClauses.Fetch, "SELECT * FROM t FETCH NEXT 1 ROWS ONLY;")]
    [InlineData(SqlClauses.Top, "SELECT TOP 1 * FROM t;")]
    [InlineData(SqlClauses.Limit, "SELECT * FROM t LIMIT 1;")]
    [InlineData(SqlClauses.Offset, "SELECT * FROM t LIMIT 1 OFFSET 1;")]
    [InlineData(SqlClauses.Values, "INSERT INTO t (id) VALUES (1);")]
    [InlineData(SqlClauses.Case, "SELECT CASE WHEN id = 1 THEN 'one' ELSE 'other' END FROM t;")]
    [InlineData(SqlClauses.Cast, "SELECT CAST('42' AS INT) FROM t;")]
    [InlineData(SqlClauses.Collate, "SELECT name FROM t WHERE name = 'Alice' COLLATE case_insensitive;")]
    [InlineData(SqlClauses.Constraint, "CREATE TABLE t (id INT, CONSTRAINT ck CHECK (id > 0));")]
    [InlineData(SqlClauses.ForeignKey, "CREATE TABLE t (parent_id INT, FOREIGN KEY (parent_id) REFERENCES parent(id));")]
    [InlineData(SqlClauses.OnUpdate, "CREATE TABLE t (parent_id INT REFERENCES parent(id) ON UPDATE CASCADE);")]
    [InlineData(SqlClauses.References, "CREATE TABLE t (parent_id INT REFERENCES parent(id));")]
    [InlineData(SqlClauses.Check, "CREATE TABLE t (id INT CHECK (id > 0));")]
    [InlineData(SqlClauses.UniqueConstraint, "CREATE TABLE t (email TEXT UNIQUE);")]
    [InlineData(SqlClauses.Cascade, "CREATE TABLE t (parent_id INT REFERENCES parent(id) ON DELETE CASCADE);")]
    [InlineData(SqlClauses.Restrict, "CREATE TABLE t (parent_id INT REFERENCES parent(id) ON DELETE RESTRICT);")]
    [InlineData(SqlClauses.Begin, "BEGIN;")]
    [InlineData(SqlClauses.IsolationLevel, "BEGIN ISOLATION LEVEL READ COMMITTED;")]
    [InlineData(SqlClauses.SetTransaction, "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;")]
    [InlineData(SqlClauses.Begin, "START TRANSACTION;")]
    [InlineData(SqlClauses.Begin, "BEGIN WORK;")]
    [InlineData(SqlClauses.Commit, "COMMIT;")]
    [InlineData(SqlClauses.Commit, "END;")]
    [InlineData(SqlClauses.Commit, "COMMIT WORK;")]
    [InlineData(SqlClauses.Savepoint, "SAVEPOINT a;")]
    [InlineData(SqlClauses.ReleaseSavepoint, "RELEASE SAVEPOINT a;")]
    [InlineData(SqlClauses.RollbackToSavepoint, "ROLLBACK TO SAVEPOINT a;")]
    [InlineData(SqlClauses.Rollback, "ROLLBACK;")]
    [InlineData(SqlClauses.Rollback, "ROLLBACK WORK;")]
    [InlineData(SqlClauses.Transaction, "BEGIN TRANSACTION;")]
    [InlineData(SqlClauses.Transaction, "COMMIT WORK;")]
    [InlineData(SqlClauses.DiscardAll, "DISCARD ALL;")]
    public void Parse_ImplementedClause_IsDeclaredAndParses(string clause, string sql)
    {
        SqlLanguageProfile.Instance.Supports(clause).ShouldBeTrue();

        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics
            .ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(SqlClauses.Using, "SELECT * FROM t JOIN u USING (id);")]
    [InlineData(SqlClauses.Cte, "WITH cte AS (SELECT * FROM t) SELECT * FROM cte;")]
    [InlineData(SqlClauses.Subquery, "SELECT * FROM t WHERE id IN (SELECT id FROM u);")]
    public void Parse_ExecutableSubquery_IsDeclaredAndReportsNoError(string clause, string sql)
    {
        // Executable forms are also measured by the live SQL conformance suite (#1021).
        SqlLanguageProfile.Instance.Supports(clause).ShouldBeTrue();

        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - JOIN: Profile advertises only executable join types")]
    [InlineData(SqlJoinType.Inner, true)]
    [InlineData(SqlJoinType.LeftOuter, true)]
    [InlineData(SqlJoinType.RightOuter, true)]
    [InlineData(SqlJoinType.FullOuter, true)]
    [InlineData(SqlJoinType.Cross, true)]
    public void Profile_JoinType_DeclaresExactExecutionSurface(SqlJoinType joinType, bool supported)
    {
        SqlLanguageProfile.SupportsJoin(joinType).ShouldBe(supported);
    }

    [Theory]
    [InlineData(SqlClauses.All)]
    [InlineData(SqlClauses.Natural)]
    public void Profile_UnsupportedClause_IsNotDeclared(string clause)
    {
        SqlLanguageProfile.Instance.Supports(clause).ShouldBeFalse();
    }

    [Theory]
    [InlineData(SqlClauses.All, "SELECT ALL id FROM t;")]
    [InlineData(SqlClauses.Natural, "SELECT * FROM t NATURAL JOIN u;")]
    public void Parse_UnsupportedClause_EmitsModelSpecificDiagnostic(string clause, string sql)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "CK00001");
        diagnostic.Message.ShouldNotBeNull();
        diagnostic.Message!.ShouldContain(clause);
        diagnostic.Message.ShouldContain("SQL");
        statement.Diagnostics.ShouldNotContain(item => item.Code == "CS00002");
    }

    [Fact]
    public void Parse_CreateUniqueIndex_DoesNotMisclassifyUniqueAsConstraint()
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(
            "CREATE UNIQUE /* retained trivia */ INDEX ix_t_email ON t (email);");

        statement.Diagnostics.ShouldNotContain(item => item.Code == "CK00001");
        statement.SqlExpression.ShouldBeOfType<SqlCreateIndexExpression>().IsUnique.ShouldBeTrue();
    }

    [Fact]
    public void Parse_UnsupportedClause_IsVisibleToAnalyzers()
    {
        var observer = new UnsupportedClauseObserver();
        var options = new QueryParserOptions();
        options.Analyzers.Add(observer);

        _ = new SqlQueryParser(options).Parse("SELECT NTH_VALUE(id,1) FROM LAST OVER (ORDER BY id GROUPS CURRENT ROW) FROM t;");

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
                .Any(item => item.Code == "CK00001");
            return Task.CompletedTask;
        }
    }
}
