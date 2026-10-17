using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>ISO 14.4 matching clauses and 16.4 clause-local WHERE conformance.</summary>
public sealed class GqlOptionalMatchParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - MATCH: clauses retain order, optional flags and local predicates")]
    [InlineData("MATCH (a) WHERE a.x = 1 OPTIONAL MATCH (a)->(b) WHERE b.x = 2 MATCH (c) RETURN a", false, true, false)]
    [InlineData("OPTIONAL MATCH (a) OPTIONAL MATCH (a)->(b) MATCH (c) RETURN a", true, true, false)]
    public void Parse_OrderedClauses_ShouldRetainEachClause(string source, bool first, bool second, bool third)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(source).ShouldBeOfType<GqlQueryStatement>();
        var query = statement.GqlExpression;
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        query.MatchClauses.Select(clause => clause.Optional).ShouldBe([first, second, third]);
        query.MatchClauses.Select(clause => clause.Paths[0].Nodes.Last().Variable).ShouldBe(["a", "b", "c"]);
        query.MatchClauses.Select(clause => clause.Predicate is not null).ShouldBe(source.Contains("WHERE") ? [true, true, false] : [false, false, false]);
        query.Matches.ShouldBeEmpty();
        query.Predicate.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - OPTIONAL MATCH: leading clauses and mutation parse")]
    [InlineData("OPTIONAL MATCH (a) RETURN a")]
    [InlineData("OPTIONAL MATCH (a) RETURN *")]
    [InlineData("OPTIONAL MATCH (a)-[r]->(b) DELETE r")]
    [InlineData("EXPLAIN OPTIONAL MATCH (a) RETURN a")]
    public void Parse_LeadingOptional_ShouldCreateOptionalClause(string source)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(source).ShouldBeOfType<GqlQueryStatement>();
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.MatchClauses.ShouldHaveSingleItem().Optional.ShouldBeTrue();
        statement.GqlExpression.Matches.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - OPTIONAL: block operands have one located capability diagnostic")]
    [InlineData("MATCH (a) OPTIONAL { MATCH (a)-[:K]->(b) } RETURN a")]
    [InlineData("MATCH (a) OPTIONAL { } RETURN a")]
    [InlineData("MATCH (a) OPTIONAL (MATCH (a)-[:K]->(b)) RETURN a")]
    [InlineData("MATCH (a) WHERE a.x = 1 OPTIONAL (MATCH (a)-[:K]->(b)) RETURN a")]
    [InlineData("OPTIONAL { MATCH (a) WHERE EXISTS { (a)->(b) } RETURN a }")]
    public void Parse_OptionalBlock_ShouldNameOperand(string source)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(source);
        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("COHDBL001");
        diagnostic.Message!.ShouldContain("OPTIONAL BLOCK OPERAND", Case.Sensitive);
        diagnostic.Message!.ShouldNotContain("FUNCTION OPTIONAL", Case.Sensitive);
        diagnostic.Start.ShouldBe(source.IndexOf("OPTIONAL", System.StringComparison.Ordinal));
        diagnostic.End!.Value.ShouldBeInRange(diagnostic.Start!.Value, source.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - MATCH: unsupported and invalid compositions keep exact codes")]
    [InlineData("MANDATORY MATCH (a) RETURN a", "COHDBL001")]
    [InlineData("OPTIONAL RETURN a", "GQL0002")]
    [InlineData("INSERT (a) MATCH (b) RETURN b", "GQL0002")]
    [InlineData("MATCH (a) DELETE a MATCH (b) RETURN b", "GQL0002")]
    public void Parse_InvalidComposition_ShouldKeepCode(string source, string code)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(source);
        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start!.Value.ShouldBeInRange(0, source.Length);
        diagnostic.End!.Value.ShouldBeInRange(diagnostic.Start!.Value, source.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - MATCH: the original constructor retains its single mandatory view")]
    public void Construct_SingleMandatory_ShouldRetainView()
    {
        // Arrange
        GqlPathPattern path = new([new GqlNodePattern("a", [], new System.Collections.Generic.Dictionary<string, GqlExpression>())], []);
        var predicate = new GqlLiteralExpression(true);
        // Act
        var query = new GqlQueryExpression([path], predicate, [], [], false, []);
        var multi = new GqlQueryExpression([new GqlMatchClause([path], predicate, false), new GqlMatchClause([path], null, false)], [], [], false, []);
        // Assert
        query.MatchClauses.ShouldHaveSingleItem().Optional.ShouldBeFalse();
        query.MatchClauses[0].Paths.ShouldBe([path]);
        query.Matches.ShouldBe([path]);
        query.Predicate.ShouldBeSameAs(predicate);
        multi.Matches.ShouldBeEmpty();
        multi.Predicate.ShouldBeNull();
    }
}
