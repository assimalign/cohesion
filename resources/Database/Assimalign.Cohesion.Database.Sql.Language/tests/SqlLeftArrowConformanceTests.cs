using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlLeftArrowConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Conformance: less-than followed by minus is a comparison (#1647)")]
    [InlineData("a<-1")]
    [InlineData("a <- 1")]
    public void Parse_LessThanMinus_ShouldCompareWithNegativeOne(string predicate)
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse($"SELECT a FROM t WHERE {predicate};").ShouldBeOfType<SqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var comparison = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Where.ShouldBeOfType<SqlBinaryExpression>();
        comparison.Operator.ShouldBe(SqlBinaryOperator.LessThan);
        SqlExpressionRenderer.Render(comparison).ShouldBe("a < -1");
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Conformance: double minus after less-than starts a comment (#1647)")]
    public void Parse_LessThanBeforeLineComment_ShouldRequireRightOperand()
    {
        // Arrange / Act
        var statement = new SqlQueryParser().Parse("SELECT a FROM t WHERE a<--1");

        // Assert
        statement.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ShouldHaveSingleItem().Code.ShouldBe("CS00003");
    }
}
