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
}
