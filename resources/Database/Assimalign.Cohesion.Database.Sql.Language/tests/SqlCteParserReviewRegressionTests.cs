using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlCteParserReviewRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: qualified FOR and aliases remain identifiers")]
    [InlineData("share", "share")]
    [InlineData("no", "no")]
    [InlineData("key", "key")]
    [InlineData("AS share", "share")]
    public void Parse_QualifiedForWithAlias_ShouldPreserveProjection(string suffix, string alias)
    {
        // Arrange / Act
        var query = Parse($"SELECT t.for {suffix} FROM t;").ShouldBeOfType<SqlSelectExpression>();

        // Assert
        var projection = query.Columns.ShouldHaveSingleItem();
        projection.Alias.ShouldBe(alias);
        var column = projection.Expression.ShouldBeOfType<SqlColumnReferenceExpression>();
        column.TableAlias.ShouldBe("t");
        column.ColumnName.ShouldBe("for");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: MERGE USING accepts keyword CTE names")]
    [InlineData("over")]
    [InlineData("partition")]
    [InlineData("window")]
    [InlineData("natural")]
    public void Parse_MergeKeywordCommonTable_ShouldRoundTrip(string name)
    {
        // Arrange
        string sql = $"WITH {name} AS (SELECT 1 AS id) MERGE INTO t USING {name} ON t.id = {name}.id WHEN NOT MATCHED THEN INSERT VALUES ({name}.id) RETURNING t.id;";

        // Act
        var merge = Parse(sql).ShouldBeOfType<SqlMergeExpression>();
        string rendered = SqlExpressionRenderer.Render(merge);

        // Assert
        merge.CommonTables.ShouldHaveSingleItem().Name.ShouldBe(name);
        SqlExpressionRenderer.Render(Parse(rendered).ShouldBeOfType<SqlMergeExpression>()).ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: recursive MERGE prefix refuses at RECURSIVE")]
    [InlineData("WITH RECURSIVE r AS (SELECT 1 AS id) MERGE INTO t USING r ON t.id=r.id WHEN MATCHED THEN DO NOTHING;")]
    [InlineData("WITH RECURSIVE r(id) AS (SELECT 1 UNION ALL SELECT id FROM r) MERGE INTO t USING r ON t.id=r.id WHEN MATCHED THEN DO NOTHING;")]
    [InlineData("EXPLAIN WITH RECURSIVE r AS (SELECT 1 AS id) MERGE INTO t USING r ON t.id=r.id WHEN MATCHED THEN DO NOTHING;")]
    public void Parse_RecursiveMergePrefix_ShouldReportCapabilityAtModifier(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        var error = statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        error.Code.ShouldBe("CK00001");
        error.Start.ShouldBe(sql.IndexOf("RECURSIVE", StringComparison.Ordinal));
        error.End.ShouldBe(error.Start + "RECURSIVE".Length);
        error.Message!.ShouldContain("WITH RECURSIVE for MERGE", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: complete SELECT locking strengths retain CK00001")]
    [InlineData("SELECT id FROM t FOR UPDATE;")]
    [InlineData("SELECT id FROM t FOR NO KEY UPDATE;")]
    [InlineData("SELECT id FROM t FOR SHARE;")]
    [InlineData("SELECT id FROM t FOR KEY SHARE;")]
    [InlineData("SELECT id FROM t ORDER BY id LIMIT 1 FOR /* strength */ UPDATE OF t NOWAIT;")]
    [InlineData("SELECT (SELECT id FROM t FOR UPDATE);")]
    [InlineData("WITH r AS (SELECT id FROM t FOR UPDATE) SELECT id FROM r;")]
    [InlineData("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n FROM r FOR UPDATE) SELECT n FROM r;")]
    public void Parse_SelectLockingClause_ShouldReportCapability(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        var error = statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ShouldHaveSingleItem();
        error.Code.ShouldBe("CK00001");
        error.Start.ShouldBe(sql.IndexOf("FOR", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: incomplete locking strengths remain syntax errors")]
    [InlineData("SELECT id FROM t FOR NO;")]
    [InlineData("SELECT id FROM t FOR KEY;")]
    [InlineData("SELECT id FROM t FOR NO KEY SHARE;")]
    [InlineData("SELECT id FROM t FOR KEY UPDATE;")]
    public void Parse_IncompleteLockingStrength_ShouldReportSyntax(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS00003");
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Code == "CK00001");
    }

    private static SqlQueryExpression Parse(string sql)
    {
        var statement = new SqlQueryParser().Parse(sql).ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error,
            string.Join("; ", statement.Diagnostics.Select(diagnostic => diagnostic.Message)));
        return statement.SqlExpression;
    }
}
