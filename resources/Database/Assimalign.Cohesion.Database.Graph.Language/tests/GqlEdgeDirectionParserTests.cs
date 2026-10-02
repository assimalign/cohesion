using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>
/// ISO/IEC 39075 16.7 edge patterns (#1139): every full and abbreviated directed form maps to its
/// <see cref="GqlPatternDirection"/>. The tilde (undirected) forms are rejected with COHDBL001,
/// pinned in <c>GqlParseStrictnessTests</c>.
/// </summary>
public sealed class GqlEdgeDirectionParserTests
{
    /// <param name="edge">The edge spelling between <c>(a)</c> and <c>(b)</c>.</param>
    /// <param name="direction">The direction it maps to.</param>
    /// <param name="abbreviated">Whether the spelling is an abbreviated edge, which has no filler.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Edges: each ISO full and abbreviated form maps to its direction")]
    [InlineData("-[r]->", GqlPatternDirection.Outgoing, false)]
    [InlineData("->", GqlPatternDirection.Outgoing, true)]
    [InlineData("<-[r]-", GqlPatternDirection.Incoming, false)]
    [InlineData("<-", GqlPatternDirection.Incoming, true)]
    [InlineData("-[r]-", GqlPatternDirection.Undirected, false)]
    [InlineData("-", GqlPatternDirection.Undirected, true)]
    [InlineData("<-[r]->", GqlPatternDirection.LeftOrRight, false)]
    [InlineData("<->", GqlPatternDirection.LeftOrRight, true)]
    [InlineData(" <-> ", GqlPatternDirection.LeftOrRight, true)]
    public void Parse_EdgeForm_ShouldMapToItsDirection(string edge, GqlPatternDirection direction, bool abbreviated)
    {
        // Arrange
        string gql = "MATCH (a)" + edge + "(b) RETURN a";

        // Act
        var statement = Parse(gql);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var path = statement.GqlExpression.Matches.ShouldHaveSingleItem();
        path.Nodes.Select(node => node.Variable).ShouldBe(["a", "b"]);
        var relationship = path.Relationships.ShouldHaveSingleItem();
        relationship.Direction.ShouldBe(direction);
        relationship.Variable.ShouldBe(abbreviated ? null : "r");
        relationship.Type.ShouldBeNull();
        relationship.LabelExpression.ShouldBeNull();
        relationship.Properties.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Edges: abbreviated forms chain with full forms in one path")]
    public void Parse_MixedEdgeChain_ShouldKeepEachDirection()
    {
        // Act
        var statement = Parse("MATCH p = (a)->(b)<-(c)-(d)<->(e)-[r:T]->(f) RETURN p");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var path = statement.GqlExpression.Matches.ShouldHaveSingleItem();
        path.Variable.ShouldBe("p");
        path.Nodes.Select(node => node.Variable).ShouldBe(["a", "b", "c", "d", "e", "f"]);
        path.Relationships.Select(edge => edge.Direction).ShouldBe([
            GqlPatternDirection.Outgoing, GqlPatternDirection.Incoming, GqlPatternDirection.Undirected,
            GqlPatternDirection.LeftOrRight, GqlPatternDirection.Outgoing]);
        path.Relationships[4].Type.ShouldBe("T");
    }

    /// <summary>
    /// '&lt;-&gt;' lexes as '&lt;-' then '&gt;' and is joined only when the two touch; an
    /// abbreviated edge carries no variable, label expression or property map; and '&lt;--' and
    /// '&lt;--&gt;' are not edges.
    /// </summary>
    /// <param name="gql">A malformed edge.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Edges: split arrows, filled abbreviated edges and Cypher left arrows report one GQL0002")]
    [InlineData("MATCH (a)<- >(b) RETURN a")]
    [InlineData("MATCH (a)- >(b) RETURN a")]
    [InlineData("MATCH (a)< ->(b) RETURN a")]
    [InlineData("MATCH (a)-r->(b) RETURN a")]
    [InlineData("MATCH (a)-:T->(b) RETURN a")]
    [InlineData("MATCH (a)-{k: 1}->(b) RETURN a")]
    [InlineData("MATCH (a)->r(b) RETURN a")]
    [InlineData("MATCH (a)<-:T-(b) RETURN a")]
    [InlineData("MATCH (a)<->[r](b) RETURN a")]
    [InlineData("INSERT (a)->:T(b)")]
    [InlineData("MATCH (a)<--(b) RETURN a")]
    [InlineData("MATCH (a)<-->(b) RETURN a")]
    [InlineData("MATCH (a)<-[r]->>(b) RETURN a")]
    [InlineData("MATCH (a)<-[r]>(b) RETURN a")]
    public void Parse_MalformedEdge_ShouldReportOneSyntaxError(string gql)
    {
        // Act
        var statement = Parse(gql);

        // Assert
        var error = statement.Diagnostics.ShouldHaveSingleItem();
        error.Code.ShouldBe("GQL0002");
        int start = error.Start.ShouldNotBeNull();
        start.ShouldBeInRange(0, gql.Length);
        error.End.ShouldNotBeNull().ShouldBeInRange(start, gql.Length);
    }

    /// <param name="gql">A quantifier after an abbreviated edge.</param>
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Edges: a quantifier after an abbreviated edge is still COHDBL001")]
    [InlineData("MATCH (a)->{1,3}(b) RETURN a")]
    [InlineData("MATCH (a)-{2}(b) RETURN a")]
    [InlineData("MATCH (a)<->{,3}(b) RETURN a")]
    [InlineData("MATCH (a){1,3} RETURN a")]
    public void Parse_QuantifierAfterEdge_ShouldStayUnsupported(string gql)
    {
        // Act
        var diagnostic = Parse(gql).Diagnostics.ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Message.ShouldBe("The QUANTIFIED PATTERN clause is not supported by the GQL surface of this database model.");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Edges: a tilde outside pattern position stays a stray character")]
    public void Parse_TildeOutsidePattern_ShouldReportSyntaxError()
    {
        // Act
        var statement = Parse("MATCH (a) WHERE a.x = ~1 RETURN a");

        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("GQL0002");
    }

    private static GqlQueryStatement Parse(string gql) => (GqlQueryStatement)new GqlQueryParser().Parse(gql);
}
