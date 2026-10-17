using System;
using System.Linq;
using System.Threading;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlWhereExpressionParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - WHERE: OR and XOR share GQL precedence and associate left")]
    [InlineData("TRUE OR TRUE XOR TRUE", GqlLogicalOperator.Xor, GqlLogicalOperator.Or)]
    [InlineData("TRUE XOR TRUE OR TRUE", GqlLogicalOperator.Or, GqlLogicalOperator.Xor)]
    public void Parse_MixedDisjunctions_ShouldAssociateLeft(string text, GqlLogicalOperator outer, GqlLogicalOperator inner)
    {
        // Arrange / Act
        var expression = Predicate(text).ShouldBeOfType<GqlLogicalExpression>();

        // Assert
        expression.Operator.ShouldBe(outer);
        expression.Operands.Count.ShouldBe(2);
        var opening = expression.Operands[0].ShouldBeOfType<GqlLogicalExpression>();
        opening.Operator.ShouldBe(inner);
        opening.Operands.Count.ShouldBe(2);
        opening.Operands.ShouldAllBe(operand => operand is GqlLiteralExpression);
        expression.Operands[1].ShouldBeOfType<GqlLiteralExpression>().Value.ShouldBe(true);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - WHERE: logical and arithmetic precedence preserve AST shape")]
    public void Parse_OperatorPrecedence_ShouldPreserveShape()
    {
        // Act
        var logical = Predicate("NOT a.x = 1 OR a.y = 2 AND a.z = 3").ShouldBeOfType<GqlLogicalExpression>();
        var numeric = Predicate("a.x + 2 * 3 >= 7").ShouldBeOfType<GqlBinaryExpression>();

        // Assert
        logical.Operator.ShouldBe(GqlLogicalOperator.Or);
        logical.Operands[0].ShouldBeOfType<GqlUnaryExpression>().Operand.ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe("=");
        logical.Operands[1].ShouldBeOfType<GqlLogicalExpression>().Operator.ShouldBe(GqlLogicalOperator.And);
        numeric.Operator.ShouldBe(">=");
        var sum = numeric.Left.ShouldBeOfType<GqlBinaryExpression>();
        sum.Operator.ShouldBe("+");
        sum.Right.ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe("*");
        Predicate("a.x = 1 XOR a.y = 2").ShouldBeOfType<GqlLogicalExpression>().Operator.ShouldBe(GqlLogicalOperator.Xor);
        Predicate("a.active").ShouldBeOfType<GqlPropertyExpression>().Property.ShouldBe("active");
        Predicate("UNKNOWN").ShouldBeOfType<GqlLiteralExpression>().Value.ShouldBeNull();
        Predicate("a.age IS NOT NULL").ShouldBeOfType<GqlIsNullExpression>().IsNegated.ShouldBeTrue();
        Predicate("(a.x = 1) IS UNKNOWN").ShouldBeOfType<GqlTruthTestExpression>().TruthValue.ShouldBeNull();
        Predicate("a.age IN [17, 42]").ShouldBeOfType<GqlInExpression>().List.ShouldBeOfType<GqlListExpression>().Items.Count.ShouldBe(2);
        Predicate("a.name STARTS WITH ('Al')").ShouldBeOfType<GqlStringPredicateExpression>().Operator.ShouldBe(GqlStringPredicateOperator.StartsWith);
        Predicate("a.n || 'x' = 'ax'").ShouldBeOfType<GqlBinaryExpression>().Left.ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe("||");
        Predicate("a.age % 5 = 2").ShouldBeOfType<GqlBinaryExpression>().Left.ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe("%");
        Predicate("a.name ENDS WITH ('ce')").ShouldBeOfType<GqlStringPredicateExpression>();
        Predicate("a.name CONTAINS ('li')").ShouldBeOfType<GqlStringPredicateExpression>();
        Predicate("(a.x = 1 OR (a.y = 2))").ShouldBeOfType<GqlLogicalExpression>().Operator.ShouldBe(GqlLogicalOperator.Or);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - WHERE: signed numbers and expression arrows retain their values")]
    public void Parse_SignedNumbers_ShouldFoldOnlyLiteralSigns()
    {
        // Act
        var arrow = Predicate("a.age<-1").ShouldBeOfType<GqlBinaryExpression>();
        var subtraction = Predicate("a.x - -1 = 0").ShouldBeOfType<GqlBinaryExpression>().Left.ShouldBeOfType<GqlBinaryExpression>();
        var minimum = Predicate("a.x = -9223372036854775808").ShouldBeOfType<GqlBinaryExpression>();

        // Assert
        arrow.Operator.ShouldBe("<");
        arrow.Right.ShouldBeOfType<GqlLiteralExpression>().Value.ShouldBe(-1L);
        subtraction.Operator.ShouldBe("-");
        subtraction.Right.ShouldBeOfType<GqlLiteralExpression>().Value.ShouldBe(-1L);
        minimum.Right.ShouldBeOfType<GqlLiteralExpression>().Value.ShouldBe(long.MinValue);
        Predicate("-a.x = +a.y").ShouldBeOfType<GqlBinaryExpression>().Left.ShouldBeOfType<GqlUnaryExpression>().Operator.ShouldBe("-");
        Parse("MATCH (a) WHERE a.active--1\n RETURN a").Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - WHERE: unsupported constructs keep targeted diagnostics")]
    [InlineData("a.b.c = 1", "COHDBL001", "NESTED PROPERTY ACCESS")]
    [InlineData("a.name LIKE 'A%'", "COHDBL001", "LIKE")]
    [InlineData("a.x BETWEEN 1 AND 2", "COHDBL001", "BETWEEN")]
    [InlineData("a.name =~ 'A.*'", "COHDBL001", "=~")]
    [InlineData("EXISTS {(a)->(b)}", "COHDBL001", "EXISTS")]
    [InlineData("ANY(x IN [1] WHERE x = 1)", "COHDBL001", "ANY")]
    [InlineData("ALL(x IN [1] WHERE x = 1)", "COHDBL001", "ALL")]
    [InlineData("NONE(x IN [1] WHERE x = 1)", "COHDBL001", "NONE")]
    [InlineData("SINGLE(x IN [1] WHERE x = 1)", "COHDBL001", "SINGLE")]
    [InlineData("a.x IN (1, 2)", "GQL0002", "[...]")]
    [InlineData("a.x = 2 = 3", "GQL0002", "chained")]
    public void Parse_ExcludedConstruct_ShouldNameIt(string predicate, string code, string construct)
    {
        // Act
        var diagnostics = Parse("MATCH (a) WHERE " + predicate + " RETURN a").Diagnostics;

        // Assert
        diagnostics.ShouldContain(d => d.Code == code && d.Message!.Contains(construct, StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - WHERE: long chains have no expression count limit")]
    [InlineData("OR", "a.x = 1")]
    [InlineData("XOR", "a.x = 1")]
    [InlineData("+", "1")]
    public void Parse_LongChain_ShouldParseWithoutLimit(string op, string leaf)
    {
        // Arrange
        string expression = string.Join(" " + op + " ", Enumerable.Repeat(leaf, 10_000));

        // Act / Assert
        Predicate(expression).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - WHERE: recursive unary and list nodes check the stack")]
    [InlineData("NOT ", "", "TRUE")]
    [InlineData("- ", "", "a.x")]
    [InlineData("(", ")", "TRUE")]
    [InlineData("1 IN [", "]", "1")]
    public void Parse_RecursiveNodes_ShouldReportStackExhaustion(string open, string close, string leaf)
    {
        // Arrange
        string gql = "MATCH (a) WHERE " + string.Concat(Enumerable.Repeat(open, 10_000)) + leaf +
            string.Concat(Enumerable.Repeat(close, 10_000)) + " RETURN a";
        GqlQueryStatement? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = Parse(gql); }
            catch (Exception error) { failure = error; } // Test thread failures must reach the assertion thread.
        }, 512 * 1024);

        // Act
        thread.Start();
        thread.Join();

        // Assert
        failure.ShouldBeNull();
        result.ShouldNotBeNull().Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0009");
    }

    private static GqlExpression Predicate(string text)
    {
        var statement = Parse("MATCH (a) WHERE " + text + " RETURN a");
        statement.Diagnostics.ShouldBeEmpty();
        return statement.GqlExpression.Predicate.ShouldNotBeNull();
    }

    private static GqlQueryStatement Parse(string text) => (GqlQueryStatement)new GqlQueryParser().Parse(text);
}
