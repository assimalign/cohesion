using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlRecursiveCteParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - WITH RECURSIVE: statement prefixes round trip")]
    [InlineData("SELECT n FROM r")]
    [InlineData("INSERT INTO t DEFAULT VALUES RETURNING id")]
    [InlineData("INSERT INTO t (id) VALUES (DEFAULT), (1) ON CONFLICT DO NOTHING RETURNING id")]
    [InlineData("INSERT INTO t (id) SELECT n FROM r ON CONFLICT (id) DO UPDATE SET id=EXCLUDED.id WHERE EXCLUDED.id>0 RETURNING id")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT ON CONSTRAINT pk DO NOTHING")]
    [InlineData("INSERT INTO t SELECT n FROM r")]
    [InlineData("UPDATE t SET id=r.n FROM r WHERE t.id=r.n")]
    [InlineData("DELETE FROM t USING r WHERE t.id=r.n")]
    [InlineData("MERGE INTO t USING r ON t.id=r.n WHEN MATCHED THEN DO NOTHING", false)]
    public void Parse_WithPrefix_ShouldPreserveSyntaxOnRoundTrip(string statement, bool recursive = true)
    {
        // Arrange
        string sql = "WITH " + (recursive ? "RECURSIVE " : "") + "r (n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<3) " + statement + ";";

        // Act
        var query = Parse(sql);
        string rendered = Render(query);
        var roundTrip = Parse(rendered);

        // Assert
        var definition = Definitions(query).ShouldHaveSingleItem();
        definition.IsRecursiveScope.ShouldBe(recursive);
        definition.Columns.ShouldBe(["n"]);
        var union = definition.Body.ShouldBeOfType<SqlSetOperationExpression>();
        union.IsAll.ShouldBeTrue();
        union.Operator.ShouldBe(SqlSetOperator.Union);
        Definitions(roundTrip).ShouldHaveSingleItem().IsRecursiveScope.ShouldBe(recursive);
        Render(roundTrip).ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - WITH RECURSIVE: query contexts retain the marker")]
    [InlineData("WITH RECURSIVE r AS (SELECT 1 AS n UNION SELECT n FROM r) SELECT n FROM r;")]
    [InlineData("WITH RECURSIVE \"natural\" AS (SELECT 1 AS n) SELECT n FROM \"natural\";")]
    [InlineData("WITH RECURSIVE top AS (SELECT 1 AS n) SELECT n FROM top;")]
    [InlineData("WITH RECURSIVE b AS (SELECT n FROM a), a AS (SELECT 1 AS n) SELECT n FROM b;")]
    [InlineData("INSERT INTO t WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) SELECT n FROM r;")]
    [InlineData("CREATE VIEW v AS WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) SELECT n FROM r;")]
    [InlineData("CREATE TABLE v AS WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) SELECT n FROM r WITH NO DATA;")]
    [InlineData("EXPLAIN WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM r) SELECT n FROM r;")]
    public void Parse_RecursiveQueryContext_ShouldRoundTrip(string sql)
    {
        // Arrange / Act
        var query = Parse(sql);
        var rendered = Render(query);

        // Assert
        rendered.ShouldContain("WITH RECURSIVE", Case.Sensitive);
        Render(Parse(rendered)).ShouldBe(rendered);
        SqlLanguageProfile.Instance.Supports(SqlClauses.Recursive).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - WITH RECURSIVE: excluded extensions retain CK00001")]
    [InlineData("WITH RECURSIVE r AS (WITH a AS (SELECT 1) SELECT * FROM a) SELECT * FROM r;")]
    [InlineData("WITH RECURSIVE r AS (MERGE INTO t USING s ON TRUE WHEN MATCHED THEN DO NOTHING) SELECT * FROM r;")]
    [InlineData("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n FROM r) SEARCH DEPTH FIRST BY n SET ord SELECT * FROM r;")]
    [InlineData("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n FROM r) CYCLE n SET cycle USING path SELECT * FROM r;")]
    [InlineData("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n FROM r FOR UPDATE) SELECT * FROM r;")]
    public void Parse_ExcludedRecursiveExtension_ShouldReportCapability(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK00001");
    }

    private static string Render(SqlQueryExpression query) => query switch
    {
        SqlQueryBodyExpression body => SqlExpressionRenderer.Render(body),
        SqlInsertExpression insert => SqlExpressionRenderer.Render(insert),
        SqlUpdateExpression update => SqlExpressionRenderer.Render(update),
        SqlDeleteExpression delete => SqlExpressionRenderer.Render(delete),
        SqlMergeExpression merge => SqlExpressionRenderer.Render(merge),
        SqlCreateTableAsExpression create => SqlExpressionRenderer.Render(create),
        SqlCreateViewExpression view => "CREATE VIEW v AS " + SqlExpressionRenderer.Render(view.Query),
        SqlExplainExpression explain => "EXPLAIN " + Render(explain.Statement),
        _ => throw new System.InvalidOperationException(),
    };

    private static SqlQueryExpression Parse(string sql)
    {
        var statement = new SqlQueryParser().Parse(sql).ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error,
            string.Join("; ", statement.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return statement.SqlExpression;
    }

    private static IReadOnlyList<SqlCommonTableExpression> Definitions(SqlQueryExpression query) => query switch
    {
        SqlQueryBodyExpression body => body.CommonTables,
        SqlInsertExpression insert => insert.CommonTables,
        SqlUpdateExpression update => update.CommonTables,
        SqlDeleteExpression delete => delete.CommonTables,
        SqlMergeExpression merge => merge.CommonTables,
        _ => [],
    };
}
