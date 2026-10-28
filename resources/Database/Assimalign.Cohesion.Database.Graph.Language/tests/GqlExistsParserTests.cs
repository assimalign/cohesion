using System.Linq;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Graph.Language.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlExistsParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - EXISTS: explicit and implicit pattern blocks round-trip")]
    [InlineData("EXISTS { (a)-[:T]->(b) }")]
    [InlineData("EXISTS { MATCH (a)-[:T]->(b) WHERE a.k = b.k }")]
    [InlineData("EXISTS { MATCH (a) MATCH (b) WHERE a.k = b.k }")]
    [InlineData("EXISTS { (a), (b) WHERE NOT EXISTS { MATCH (b)->(c) } }")]
    [InlineData("EXISTS { OPTIONAL MATCH (a) WHERE a.k = $k }")]
    [InlineData("EXISTS { MATCH ANY (a)-[:T]->(b) }")]
    public void Parse_PatternBlock_ShouldRoundTrip(string source)
    {
        // Arrange
        var first = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN " + source);
        first.Diagnostics.ShouldBeEmpty();
        var expression = first.GqlExpression.Projections[0].Expression.ShouldBeOfType<GqlExistsExpression>();
        // Act
        var second = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN " + expression);
        // Assert
        second.Diagnostics.ShouldBeEmpty();
        GqlSemanticTree.Expression(second.GqlExpression.Projections[0].Expression).ShouldBe(GqlSemanticTree.Expression(expression));
        expression.Location!.Start.ShouldBe(7);
        expression.Location.End.ShouldBe(7 + source.Length);
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - EXISTS: excluded query forms retain CK00001")]
    [InlineData("EXISTS ((a))")]
    [InlineData("EXISTS { RETURN 1 }")]
    [InlineData("EXISTS { MATCH (a) RETURN a }")]
    [InlineData("EXISTS { MATCH (a) NEXT MATCH (b) }")]
    [InlineData("EXISTS { MATCH (a) UNION MATCH (b) }")]
    [InlineData("EXISTS { CALL something() }")]
    [InlineData("EXISTS { MATCH (a) ORDER BY a.k }")]
    [InlineData("EXISTS { MATCH (a) LIMIT 1 }")]
    [InlineData("EXISTS { CREATE (a) }")]
    [InlineData("EXISTS { INSERT (a) }")]
    [InlineData("EXISTS { MATCH (a) SET a.k = 1 }")]
    [InlineData("EXISTS { MATCH (a) DELETE a }")]
    [InlineData("COUNT { MATCH (a) }")]
    [InlineData("COLLECT { MATCH (a) }")]
    [InlineData("(a)-->(b)")]
    [InlineData("(a)--(b)")]
    [InlineData("(a)<--(b)")]
    [InlineData("(a)-[:T]->(b)")]
    public void Parse_QueryForm_ShouldRefuseCapability(string source)
    {
        // Arrange / Act
        var errors = new GqlQueryParser().Parse("MATCH (a) WHERE " + source + " RETURN a").Diagnostics;
        // Assert
        errors.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - EXISTS: bare-pattern refusal preserves ordinary signed arithmetic")]
    [InlineData("RETURN (3) - -1")]
    [InlineData("RETURN (3)<-(1)")]
    [InlineData("RETURN (3) < - (1)")]
    public void Parse_OrdinaryArithmetic_ShouldRemainSupported(string source)
    {
        // Arrange / Act / Assert
        new GqlQueryParser().Parse(source).Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - EXISTS: empty or unbraced patterns have a syntax identity")]
    [InlineData("EXISTS {}")]
    [InlineData("EXISTS { MATCH }")]
    [InlineData("EXISTS TRUE")]
    public void Parse_MissingPattern_ShouldReportSyntax(string source)
    {
        // Arrange / Act / Assert
        new GqlQueryParser().Parse("RETURN " + source).Diagnostics.Single().Code.ShouldBe("CG00013");
    }
}
