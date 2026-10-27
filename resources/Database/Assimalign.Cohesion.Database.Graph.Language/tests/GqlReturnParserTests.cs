using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlReturnParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - RETURN: set quantifier and property expression survive parsing")]
    [InlineData("DISTINCT", GqlSetQuantifier.Distinct)]
    [InlineData("ALL", GqlSetQuantifier.All)]
    [InlineData("", GqlSetQuantifier.All)]
    public void Parse_SetQuantifier_ShouldPreserveProjection(string quantifier, GqlSetQuantifier expected)
    {
        // Arrange / Act
        var statement = Parse($"MATCH (a) RETURN {quantifier} a.name AS person");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.SetQuantifier.ShouldBe(expected);
        statement.GqlExpression.ReturnStar.ShouldBeFalse();
        var projection = statement.GqlExpression.Projections.ShouldHaveSingleItem();
        var property = projection.Expression.ShouldBeOfType<GqlPropertyExpression>();
        property.Variable.ShouldBe("a");
        property.Property.ShouldBe("name");
        projection.Alias.ShouldBe("person");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - RETURN: computed and standalone expressions carry aliases and normalized source")]
    public void Parse_ComputedItems_ShouldPreserveExpressionsAliasesAndText()
    {
        // Arrange / Act
        var statement = Parse("MATCH (a) RETURN a . age /* comment */ + 1 AS next, a.name || '!'");
        var standalone = Parse("RETURN 1 + 1 AS two");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var projections = statement.GqlExpression.Projections;
        projections.Count.ShouldBe(2);
        var addition = projections[0].Expression.ShouldBeOfType<GqlBinaryExpression>();
        addition.Operator.ShouldBe("+");
        addition.Left.ShouldBeOfType<GqlPropertyExpression>().Property.ShouldBe("age");
        addition.Right.ShouldBeOfType<GqlLiteralExpression>().Value.ShouldBe(1L);
        addition.Text.ShouldBe("a.age + 1");
        projections[0].Alias.ShouldBe("next");
        projections[1].Expression.ShouldBeOfType<GqlBinaryExpression>().Operator.ShouldBe("||");
        projections[1].Expression.Text.ShouldBe("a.name || '!'");
        projections[1].Alias.ShouldBeNull();
        standalone.Diagnostics.ShouldBeEmpty();
        standalone.GqlExpression.Matches.ShouldBeEmpty();
        standalone.GqlExpression.Projections.ShouldHaveSingleItem().Alias.ShouldBe("two");
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - RETURN: parameters are live expressions with complete spans")]
    [InlineData("MATCH (a) RETURN $p", "$p")]
    [InlineData("MATCH (a) RETURN @p AS p", "@p")]
    [InlineData("RETURN $p AS p", "$p")]
    public void Parse_ParameterProjection_ShouldPreserveReference(string source, string reference)
    {
        // Arrange / Act
        var statement = Parse(source);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var parameter = statement.GqlExpression.Projections.Single().Expression.ShouldBeOfType<GqlParameterExpression>();
        parameter.Name.ShouldBe("p");
        parameter.Location!.Start.ShouldBe(source.IndexOf(reference, StringComparison.Ordinal));
        parameter.Location.End.ShouldBe(parameter.Location.Start + reference.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - RETURN: star is a distinct AST member")]
    [InlineData("MATCH (a)-[r]->(b) RETURN *", GqlSetQuantifier.All)]
    [InlineData("MATCH (a) RETURN DISTINCT *", GqlSetQuantifier.Distinct)]
    [InlineData("INSERT (a) RETURN ALL *", GqlSetQuantifier.All)]
    public void Parse_Star_ShouldKeepSeparateFromItems(string source, GqlSetQuantifier quantifier)
    {
        // Arrange / Act
        var statement = Parse(source);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.ReturnStar.ShouldBeTrue();
        statement.GqlExpression.SetQuantifier.ShouldBe(quantifier);
        statement.GqlExpression.Projections.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - RETURN: null, truth and labeled tests use expression grammar")]
    [InlineData("MATCH (a) RETURN a IS LABELED Person")]
    [InlineData("MATCH (a) RETURN a IS NULL, a.active IS NOT TRUE")]
    [InlineData("RETURN 1<-2 AS less, NOT FALSE AS yes")]
    [InlineData("RETURN 2 * 3 AS product, 1 IN [1, NULL] AS member")]
    public void Parse_PredicatesInReturn_ShouldAcceptExpressions(string source)
    {
        // Arrange / Act
        var statement = Parse(source);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.Projections.ShouldNotBeEmpty();
        if (source.Contains("1<-2", StringComparison.Ordinal))
        { statement.GqlExpression.Projections[0].Expression.Text.ShouldBe("1 < - 2"); }
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - RETURN: excluded compositions keep precise capability diagnostics")]
    [InlineData("MATCH (a) RETURN a {.name}", "MAP PROJECTION")]
    [InlineData("MATCH (a) DELETE a RETURN *", "RETURN AFTER DELETE")]
    public void Parse_ExcludedForm_ShouldNameConstruct(string source, string construct)
    {
        // Arrange / Act
        var diagnostic = Parse(source).Diagnostics.ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message!.ShouldContain(construct, Case.Sensitive);
    }

    private static GqlQueryStatement Parse(string source) => (GqlQueryStatement)new GqlQueryParser().Parse(source);
}
