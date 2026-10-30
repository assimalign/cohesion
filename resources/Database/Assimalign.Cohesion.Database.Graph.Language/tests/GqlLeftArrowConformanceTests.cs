using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlLeftArrowConformanceTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Conformance: relationship arrows retain their direction (#1647)")]
    [InlineData("<-[r]-", GqlPatternDirection.Incoming)]
    [InlineData("<-", GqlPatternDirection.Incoming)]
    [InlineData("<-[r]->", GqlPatternDirection.LeftOrRight)]
    [InlineData("<->", GqlPatternDirection.LeftOrRight)]
    [InlineData("-[r]->", GqlPatternDirection.Outgoing)]
    [InlineData("->", GqlPatternDirection.Outgoing)]
    public void Parse_RelationshipArrow_ShouldKeepDirection(string arrow, GqlPatternDirection expected)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse($"MATCH (a){arrow}(b) RETURN a, b").ShouldBeOfType<GqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.Matches.ShouldHaveSingleItem().Relationships.ShouldHaveSingleItem().Direction.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - JSON isolation: excluded Cypher arrows retain their existing diagnostics (#1722)")]
    [InlineData("-->", "CG00008")]
    [InlineData("<--", "CG00002")]
    [InlineData("<-->", "CG00002")]
    public void Parse_CypherArrows_ShouldRetainRefusal(string arrow, string code)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse($"MATCH (a){arrow}(b) RETURN a, b");
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }
}
