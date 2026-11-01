using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlPartialIndexParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Partial indexes: predicates survive canonical statement rendering (#1630)")]
    [InlineData("CREATE INDEX ix ON t(a) WHERE active AND a > 0")]
    [InlineData("CREATE UNIQUE INDEX IF NOT EXISTS ix ON dbo.t(a,b) WHERE b IS NOT NULL OR a BETWEEN 1 AND 3")]
    public void Parse_PartialIndex_ShouldRoundTrip(string sql)
    {
        // Arrange
        var parser = new SqlQueryParser();
        var statement = parser.Parse(sql).ShouldBeOfType<SqlQueryStatement>();
        var index = statement.SqlExpression.ShouldBeOfType<SqlCreateIndexExpression>();

        // Act
        string canonical = SqlExpressionRenderer.Render(index);
        var reloaded = parser.Parse(canonical).ShouldBeOfType<SqlQueryStatement>().SqlExpression.ShouldBeOfType<SqlCreateIndexExpression>();

        // Assert
        statement.Diagnostics.ShouldNotContain(d => d.Severity == Types.DiagnosticSeverity.Error);
        index.Predicate.ShouldNotBeNull();
        SqlExpressionRenderer.Render(reloaded).ShouldBe(canonical);
        reloaded.IsUnique.ShouldBe(index.IsUnique);
    }
}
