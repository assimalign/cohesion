using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlLeftArrowConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Conformance: less-than followed by minus is a comparison (#1647)")]
    [InlineData("a<-1")]
    [InlineData("a <- 1")]
    public void Parse_LessThanMinus_ShouldCompareWithNegativeOne(string predicate)
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse($"SELECT * FROM t WHERE {predicate}").ShouldBeOfType<OqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var comparison = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Predicate.ShouldBeOfType<OqlBinaryExpression>();
        comparison.Operator.ShouldBe("<");
        comparison.Left.ShouldBeOfType<OqlPathExpression>();
        var negative = comparison.Right.ShouldBeOfType<OqlUnaryExpression>();
        negative.Operator.ShouldBe("-");
        negative.Operand.ShouldBeOfType<OqlLiteralExpression>().Value.ShouldBeOfType<JsonElement>().GetDecimal().ShouldBe(1m);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Conformance: double minus after less-than starts a comment (#1647)")]
    public void Parse_LessThanBeforeLineComment_ShouldRequireRightOperand()
    {
        // Arrange / Act
        var statement = new OqlQueryParser().Parse("SELECT * FROM t WHERE a<--1");

        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00002");
    }
}
