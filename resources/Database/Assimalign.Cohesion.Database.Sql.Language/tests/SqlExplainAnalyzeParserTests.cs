using System.Linq;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlExplainAnalyzeParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - EXPLAIN ANALYZE: contextual prefix retains its SELECT body (#1522)")]
    [InlineData("EXPLAIN ANALYZE SELECT 1")]
    [InlineData("EXPLAIN /* plan */ ANALYZE SELECT 1 UNION SELECT 2")]
    [InlineData("EXPLAIN ANALYZE WITH c AS (SELECT 1 AS n) SELECT n FROM c")]
    public void Parse_ExplainAnalyze_ShouldRetainAnalysisFlag(string sql)
    {
        var statement = new SqlQueryParser().Parse(sql + ";").ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.ShouldBeEmpty(string.Join("; ", statement.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        var explain = statement.SqlExpression.ShouldBeOfType<SqlExplainExpression>();
        explain.Analyze.ShouldBeTrue();
        explain.Statement.ShouldBeAssignableTo<SqlQueryBodyExpression>();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - EXPLAIN: ordinary descriptions stay nonexecuting and ANALYZE is a contextual identifier (#1522)")]
    [InlineData("EXPLAIN SELECT analyze FROM analyze")]
    [InlineData("EXPLAIN DELETE FROM analyze")]
    public void Parse_PlainExplain_ShouldRemainNonexecuting(string sql)
    {
        var statement = new SqlQueryParser().Parse(sql + ";").ShouldBeOfType<SqlQueryStatement>();
        statement.Diagnostics.ShouldBeEmpty(string.Join("; ", statement.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        statement.SqlExpression.ShouldBeOfType<SqlExplainExpression>().Analyze.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - ANALYZE: standalone statistics maintenance stays unsupported (#1522)")]
    public void Parse_StandaloneAnalyze_ShouldReportDiagnostic()
        => new SqlQueryParser().Parse("ANALYZE t;").Diagnostics.ShouldContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
