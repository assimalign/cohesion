using System.Linq;

using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public class SqlExecutionSurfaceDiagnosticTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - JOIN: Accepts two-table INNER JOIN with ON")]
    [InlineData("SELECT * FROM t JOIN u ON t.id = u.id;")]
    [InlineData("SELECT * FROM t INNER JOIN u ON t.id = u.id;")]
    [InlineData("select * from t inner /* trivia */ join u on t.id = u.id;")]
    [InlineData("SELECT * FROM INFORMATION_SCHEMA.TABLES t JOIN u ON t.TABLE_NAME = u.name;")]
    [InlineData("SELECT * FROM t JOIN COHESION_SCHEMA.INDEXES i ON t.name = i.TABLE_NAME;")]
    [InlineData("SELECT * FROM t LEFT JOIN u ON t.id = u.id;")]
    [InlineData("SELECT * FROM t LEFT OUTER JOIN u ON t.id = u.id;")]
    [InlineData("SELECT * FROM t CROSS JOIN u;")]
    [InlineData("SELECT * FROM t, u, v;")]
    [InlineData("SELECT * FROM t JOIN u USING (id, name);")]
    [InlineData("SELECT * FROM t LEFT JOIN u USING (id);")]
    [InlineData("SELECT * FROM t JOIN u ON t.id = u.id LEFT JOIN v USING (name) CROSS JOIN w;")]
    [InlineData("SELECT * FROM t, u JOIN v USING (id), w;")]
    [InlineData("SELECT * FROM t LEFT JOIN INFORMATION_SCHEMA.COLUMNS c ON t.id = c.ORDINAL_POSITION;")]
    [InlineData("SELECT * FROM INFORMATION_SCHEMA.TABLES t JOIN INFORMATION_SCHEMA.COLUMNS c USING (TABLE_NAME) LEFT JOIN u ON u.id = c.ORDINAL_POSITION;")]
    public void Parse_InnerJoin_ReportsNoError(string sql)
    {
        SqlLanguageProfile.Instance.Supports(SqlClauses.Join).ShouldBeTrue();

        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - JOIN: Excludes unimplemented join types")]
    [InlineData("SELECT * FROM t RIGHT JOIN u ON t.id = u.id;", "RIGHT OUTER JOIN", "RIGHT JOIN")]
    [InlineData("SELECT * FROM t RIGHT OUTER JOIN u ON t.id = u.id;", "RIGHT OUTER JOIN", "RIGHT OUTER JOIN")]
    [InlineData("SELECT * FROM t FULL OUTER JOIN u ON t.id = u.id;", "FULL OUTER JOIN", "FULL OUTER JOIN")]
    public void Parse_UnimplementedJoinType_ReportsPreciseModelDiagnostic(string sql, string form, string locationText)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "CK00001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Message!.ShouldContain(form);
        diagnostic.Message!.ShouldContain("INNER, LEFT [OUTER] and CROSS JOIN");
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe(locationText);
        statement.Diagnostics.ShouldNotContain(item => item.Code == "CS00002");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - JOIN: Reports the explicit execution boundary")]
    [InlineData("SELECT * FROM t JOIN u;", "require ON or USING")]
    [InlineData("SELECT t.* FROM t JOIN u ON t.id = u.id;", "Qualified SQL wildcard")]
    [InlineData("SELECT dbo.t.* FROM dbo.t JOIN dbo.u ON dbo.t.id = dbo.u.id;", "Qualified SQL wildcard")]
    public void Parse_UnsupportedJoinShape_ReportsPreciseModelDiagnostic(string sql, string message)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "CK00001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Message!.ShouldContain(message);
        statement.Diagnostics.ShouldNotContain(item => item.Code == "CS00002");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - JOIN: Rejects incomplete INNER JOIN syntax")]
    [InlineData("SELECT * FROM t INNER u ON t.id = u.id;")]
    [InlineData("SELECT * FROM t INNER JOIN u ON;")]
    [InlineData("SELECT * FROM t INNER JOIN u ON WHERE t.id = 1;")]
    public void Parse_IncompleteInnerJoin_ReportsSyntaxDiagnostic(string sql)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldContain(item => item.Code == "CS00003");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Grouping: Restores executable grouping clauses")]
    [InlineData(SqlClauses.GroupBy, "SELECT id FROM t GROUP BY id;", "GROUP BY")]
    [InlineData(SqlClauses.GroupBy, "SELECT id FROM t group /* trivia */ by id;", "group /* trivia */ by")]
    [InlineData(SqlClauses.GroupBy, "SELECT id FROM t GROUP BY id HAVING COUNT(*) > 0;", "GROUP BY")]
    [InlineData(SqlClauses.Having, "SELECT id FROM t HAVING id > 0;", "HAVING")]
    [InlineData(SqlClauses.GroupBy, "SELECT t.id FROM t JOIN u ON t.id = u.id GROUP BY t.id;", "GROUP BY")]
    public void Parse_GroupingClause_ReportsNoError(string clause, string sql, string clauseText)
    {
        SqlLanguageProfile.Instance.Supports(clause).ShouldBeTrue();
        sql.ShouldContain(clauseText);

        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData(SqlClauses.Subquery, "SELECT * FROM t WHERE id IN (SELECT id FROM u);", "SELECT")]
    [InlineData(SqlClauses.Subquery, "SELECT * FROM t WHERE id NOT IN (SELECT id FROM u);", "SELECT")]
    [InlineData(SqlClauses.Subquery, "SELECT * FROM t WHERE EXISTS (SELECT id FROM u);", "SELECT")]
    [InlineData(SqlClauses.Subquery, "SELECT * FROM t WHERE NOT EXISTS (SELECT id FROM u);", "SELECT")]
    [InlineData(SqlClauses.Subquery, "SELECT (SELECT id FROM u) FROM t;", "SELECT")]
    [InlineData(SqlClauses.Subquery, "INSERT INTO t (id) SELECT id FROM u;", "SELECT")]
    [InlineData(SqlClauses.Subquery, "SELECT t.id FROM t JOIN u ON t.id = u.id WHERE u.id IN (SELECT id FROM v);", "SELECT")]
    [InlineData(SqlClauses.Subquery, "SELECT id FROM t GROUP BY id HAVING id IN (SELECT id FROM u);", "SELECT")]
    public void Parse_ExecutableSubquery_ReportsNoError(string clause, string sql, string locationText)
    {
        SqlLanguageProfile.Instance.Supports(clause).ShouldBeTrue();
        sql.ShouldContain(locationText);

        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - Subquery: Reports retained execution boundaries")]

    [InlineData("UPDATE t SET id = (SELECT id FROM u);", "SUBQUERY in UPDATE", "SELECT")]

    public void Parse_UnsupportedSubquery_ReportsModelDiagnostic(string sql, string form, string locationText)
    {

        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "CK00001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Message!.ShouldContain(form);
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe(locationText);
        statement.Diagnostics.ShouldNotContain(item => item.Code == "CS00002");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql.Language] - CAST: Accepts supported scalar contexts")]
    [InlineData("SELECT CAST('42' AS INT) FROM t;")]
    [InlineData("select cast /* trivia */ ('42' as int) from t;")]
    [InlineData("SELECT id FROM t WHERE CAST(id AS TEXT) = '1';")]
    [InlineData("SELECT id FROM t ORDER BY CAST(id AS TEXT);")]
    [InlineData("SELECT id FROM t LIMIT CAST('1' AS INT);")]
    [InlineData("INSERT INTO t VALUES (CAST('42' AS INT));")]
    [InlineData("UPDATE t SET id = CAST('42' AS INT);")]
    [InlineData("DELETE FROM t WHERE id = CAST('42' AS INT);")]
    [InlineData("CREATE TABLE t (id INT CHECK (CAST(id AS TEXT) <> ''));")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT ck CHECK (CAST(id AS TEXT) <> '');")]
    public void Parse_CastInScalarContext_ReportsNoError(string sql)
    {
        // Real conversion is verified by the SQL engine and wire execution suites.
        SqlLanguageProfile.Instance.Supports(SqlClauses.Cast).ShouldBeTrue();

        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("SELECT 'JOIN GROUP BY HAVING (SELECT)' FROM t;")]
    [InlineData("SELECT \"JOIN\", \"GROUP\", \"HAVING\", \"SELECT\" FROM t;")]
    [InlineData("/* SELECT */ SELECT id FROM t /* JOIN u GROUP BY id HAVING id > 0 */ WHERE id > 0;")]
    [InlineData("SELECT 'CAST(42 AS TEXT)' FROM t;")]
    [InlineData("SELECT \"CAST\" FROM t;")]
    [InlineData("SELECT id FROM t /* CAST(id AS TEXT) */ WHERE id > 0;")]
    [InlineData("SELECT id AS cast FROM t;")]
    [InlineData("SELECT id FROM t AS cast;")]
    [InlineData("CREATE TABLE cast (id INT);")]
    [InlineData("CREATE TABLE t (cast INT);")]
    public void Parse_UnsupportedWordsInLiteralsIdentifiersAndComments_RemainsSupported(string sql)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
    }
}
