using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlOperatorBoundaryTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - IS: predicate operands retain SQL++ presence tests")]
    [InlineData("n = 1 IS NOT NULL", "right", "IS NOT NULL")]
    [InlineData("n != 1 IS NOT NULL", "right", "IS NOT NULL")]
    [InlineData("n < 1 IS NOT NULL", "right", "IS NOT NULL")]
    [InlineData("n <= 1 IS NOT NULL", "right", "IS NOT NULL")]
    [InlineData("n > 1 IS NOT NULL", "right", "IS NOT NULL")]
    [InlineData("n >= 1 IS NOT NULL", "right", "IS NOT NULL")]
    [InlineData("1 = NULL IS NULL", "right", "IS NULL")]
    [InlineData("1 IN [1] IS NULL", "array", "IS NULL")]
    [InlineData("2 BETWEEN 1 AND 3 IS NOT NULL", "upper", "IS NOT NULL")]
    [InlineData("2 BETWEEN 1 IS NOT NULL AND 3", "lower", "IS NOT NULL")]
    [InlineData("'abc' LIKE 'a%' IS NOT NULL", "pattern", "IS NOT NULL")]
    [InlineData("'a%' LIKE 'a!%' ESCAPE '!' IS NOT NULL", "escape", "IS NOT NULL")]
    public void Parse_IsBoundary_ShouldBindInsidePredicateOperand(string expression, string position, string operation)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse("SELECT " + expression + " FROM items");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var node = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections.Single().Expression;
        var operand = (node, position) switch
        {
            (OqlBinaryExpression binary, "right") => binary.Right,
            (OqlInExpression membership, "array") => membership.Array!,
            (OqlBetweenExpression between, "lower") => between.Lower,
            (OqlBetweenExpression between, "upper") => between.Upper,
            (OqlLikeExpression like, "pattern") => like.Pattern,
            (OqlLikeExpression like, "escape") => like.Escape!,
            _ => throw new InvalidOperationException(),
        };
        operand.ShouldBeOfType<OqlUnaryExpression>().Operator.ShouldBe(operation);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Operators: concatenation and distinctness retain their ASTs")]
    [InlineData("a || b", "CONCAT")]
    [InlineData("a IS DISTINCT FROM b", "IS DISTINCT FROM")]
    [InlineData("a IS NOT DISTINCT FROM b", "IS NOT DISTINCT FROM")]
    public void Parse_OperatorVocabulary_ShouldRetainSupportedOperation(string expression, string operation)
    {
        // Arrange / Act
        var statement = (OqlQueryStatement)new OqlQueryParser().Parse("SELECT " + expression + " FROM items");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var node = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections.Single().Expression;
        (node is OqlCallExpression call ? call.Name : node.ShouldBeOfType<OqlBinaryExpression>().Operator).ShouldBe(operation);
    }
}
