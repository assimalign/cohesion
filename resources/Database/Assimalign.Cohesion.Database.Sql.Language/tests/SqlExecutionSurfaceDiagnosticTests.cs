using System.Linq;

using Assimalign.Cohesion.Database.Language;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public class SqlExecutionSurfaceDiagnosticTests
{
    [Theory]
    [InlineData(SqlClauses.Join, "SELECT * FROM t JOIN u ON t.id = u.id;", "JOIN")]
    [InlineData(SqlClauses.Join, "SELECT * FROM t INNER JOIN u ON t.id = u.id;", "JOIN")]
    [InlineData(SqlClauses.Join, "SELECT * FROM t LEFT JOIN u ON t.id = u.id;", "JOIN")]
    [InlineData(SqlClauses.Join, "SELECT * FROM t RIGHT OUTER JOIN u ON t.id = u.id;", "JOIN")]
    [InlineData(SqlClauses.Join, "SELECT * FROM t FULL OUTER JOIN u ON t.id = u.id;", "JOIN")]
    [InlineData(SqlClauses.Join, "SELECT * FROM t CROSS JOIN u;", "JOIN")]
    [InlineData(SqlClauses.Join, "select * from t inner /* trivia */ join u on t.id = u.id;", "join")]
    [InlineData(SqlClauses.GroupBy, "SELECT id FROM t GROUP BY id;", "GROUP BY")]
    [InlineData(SqlClauses.GroupBy, "SELECT id FROM t group /* trivia */ by id;", "group /* trivia */ by")]
    [InlineData(SqlClauses.GroupBy, "SELECT id FROM t GROUP BY id HAVING COUNT(*) > 0;", "GROUP BY")]
    [InlineData(SqlClauses.Having, "SELECT id FROM t HAVING id > 0;", "HAVING")]
    [InlineData(SqlClauses.Subquery, "SELECT * FROM t WHERE id IN (SELECT id FROM u);", "SELECT")]
    [InlineData(SqlClauses.Subquery, "SELECT * FROM t WHERE id NOT IN (SELECT id FROM u);", "SELECT")]
    [InlineData(SqlClauses.Subquery, "SELECT * FROM t WHERE EXISTS (SELECT id FROM u);", "SELECT")]
    [InlineData(SqlClauses.Subquery, "SELECT * FROM t WHERE NOT EXISTS (SELECT id FROM u);", "SELECT")]
    [InlineData(SqlClauses.Subquery, "SELECT (SELECT id FROM u) FROM t;", "SELECT")]
    [InlineData(SqlClauses.Subquery, "SELECT * FROM (SELECT id FROM u) derived;", "SELECT")]
    [InlineData(SqlClauses.Subquery, "INSERT INTO t (id) SELECT id FROM u;", "SELECT")]
    [InlineData(SqlClauses.Subquery, "UPDATE t SET id = (SELECT id FROM u);", "SELECT")]
    [InlineData(SqlClauses.Subquery, "DELETE FROM t WHERE id IN (SELECT id FROM u);", "SELECT")]
    public void Parse_ClauseAwaitingExecution_ReportsModelDiagnostic(string clause, string sql, string locationText)
    {
        SqlLanguageProfile.Instance.Supports(clause).ShouldBeFalse();

        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        var diagnostic = statement.Diagnostics.Single(item => item.Code == "COHDBL001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Message.ShouldBe($"The {clause} clause is not supported by the SQL surface of this database model.");
        sql[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe(locationText);
        statement.Diagnostics.ShouldNotContain(item => item.Code == "SQL0002");
    }

    [Theory]
    [InlineData("SELECT 'JOIN GROUP BY HAVING (SELECT)' FROM t;")]
    [InlineData("SELECT \"JOIN\", \"GROUP\", \"HAVING\", \"SELECT\" FROM t;")]
    [InlineData("/* SELECT */ SELECT id FROM t /* JOIN u GROUP BY id HAVING id > 0 */ WHERE id > 0;")]
    public void Parse_UnsupportedWordsInLiteralsIdentifiersAndComments_RemainsSupported(string sql)
    {
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse(sql);

        statement.Diagnostics.ShouldNotContain(item => item.Severity == DiagnosticSeverity.Error);
    }
}
