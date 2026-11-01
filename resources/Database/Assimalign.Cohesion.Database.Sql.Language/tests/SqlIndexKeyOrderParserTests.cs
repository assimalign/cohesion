using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlIndexKeyOrderParserTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Index key order: defaults and explicit NULL placement round trip (#1631)")]
    [InlineData("a ASC NULLS LAST,b DESC NULLS FIRST,c ASC NULLS FIRST,d DESC NULLS LAST", "a, b DESC, c NULLS FIRST, d DESC NULLS LAST")]
    [InlineData("a,b DESC,c NULLS FIRST,d DESC NULLS LAST", "a, b DESC, c NULLS FIRST, d DESC NULLS LAST")]
    public void Parse_KeyOptions_ShouldNormalizeAndRoundTrip(string columns, string expected)
    {
        // Arrange
        var parser = new SqlQueryParser();
        var statement = parser.Parse("CREATE UNIQUE INDEX ix ON t(" + columns + ") WHERE a > 0").ShouldBeOfType<SqlQueryStatement>();
        var index = statement.SqlExpression.ShouldBeOfType<SqlCreateIndexExpression>();

        // Act
        string text = SqlExpressionRenderer.Render(index);
        var restored = parser.Parse(text).ShouldBeOfType<SqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        index.KeyOrders.ShouldBe([SqlIndexKeyOrder.AscendingNullsLast, SqlIndexKeyOrder.DescendingNullsFirst,
            SqlIndexKeyOrder.AscendingNullsFirst, SqlIndexKeyOrder.DescendingNullsLast]);
        text.ShouldBe("CREATE UNIQUE INDEX ix ON t (" + expected + ") WHERE a > 0");
        restored.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        restored.SqlExpression.ShouldBeOfType<SqlCreateIndexExpression>().KeyOrders.ShouldBe(index.KeyOrders);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Constraint keys: index directions are syntax errors (#1631)")]
    [InlineData("CREATE TABLE t(a INT, PRIMARY KEY(a DESC))")]
    [InlineData("CREATE TABLE t(a INT, UNIQUE(a ASC))")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT uq UNIQUE(a NULLS FIRST)")]
    public void Parse_ConstraintKeyOptions_ShouldRefuseWithSyntaxCode(string sql)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse(sql);

        // Assert
        statement.Diagnostics.ShouldContain(d => d.Code == "CS00003" && d.Severity == DiagnosticSeverity.Error);
    }
}
