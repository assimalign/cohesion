using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Graph.Language.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlPatternComprehensionParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - Pattern comprehensions: parse and render lexical pattern operands")]
    [InlineData("[(a)->(b) | b.k]", false, false)]
    [InlineData("[p=(a)-[r:T]->(b) WHERE b.k > 1 | p]", true, true)]
    [InlineData("[(a)<-[r:T]-(b) WHERE b:A|B | r.w]", false, true)]
    [InlineData("[(a)-[:T*0..2]->(b) | [x IN [b] | x.k]]", false, false)]
    [InlineData("[(a)->(b) | [(b)->(c) | c.k]]", false, false)]
    public void Parse_PatternCollection_ShouldRoundTrip(string source, bool path, bool filter)
    {
        // Arrange / Act
        var first = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN " + source);
        // Assert
        first.Diagnostics.ShouldBeEmpty();
        var expression = first.GqlExpression.Projections[0].Expression.ShouldBeOfType<GqlPatternComprehensionExpression>();
        (expression.Pattern.Variable is not null).ShouldBe(path);
        (expression.Predicate is not null).ShouldBe(filter);
        expression.Location!.Start.ShouldBe(7);
        expression.Location.End.ShouldBe(source.Length + 7);
        var second = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN " + expression);
        second.Diagnostics.ShouldBeEmpty();
        GqlSemanticTree.Expression(second.GqlExpression.Projections[0].Expression).ShouldBe(GqlSemanticTree.Expression(expression));
    }

    public static IEnumerable<object[]> QuantifiedRelationshipCases
    {
        get
        {
            foreach (string edge in new[] { "->", "<-", "-", "<->", "-[r]->", "<-[r]-", "-[r]-", "<-[r]->" })
            {
                foreach (string quantifier in new[] { "{1,2}", "{2}", "{1,}", "{,2}", "{,}", "+", "*" })
                { yield return [edge, quantifier]; }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Pattern comprehensions: every quantified relationship matches the MATCH grammar (#1736)")]
    [MemberData(nameof(QuantifiedRelationshipCases))]
    public void Parse_QuantifiedPatternCollection_ShouldMatchAndRoundTrip(string edge, string quantifier)
    {
        // Arrange
        string pattern = "(a)" + edge + quantifier + "(b)";
        var match = (GqlQueryStatement)new GqlQueryParser().Parse("MATCH p=" + pattern + " RETURN p");
        match.Diagnostics.ShouldBeEmpty();
        var expected = match.GqlExpression.MatchClauses[0].Paths.ShouldHaveSingleItem();
        foreach (bool named in new[] { true, false })
        {
            // Act
            var statement = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN [" + (named ? "p=" : "") + pattern + " | b]");
            // Assert
            statement.Diagnostics.ShouldBeEmpty();
            var expression = statement.GqlExpression.Projections[0].Expression.ShouldBeOfType<GqlPatternComprehensionExpression>();
            GqlSemanticTree.Path(expression.Pattern).ShouldBe(GqlSemanticTree.Path(expected with { Variable = named ? "p" : null }));
            var rendered = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN " + expression);
            rendered.Diagnostics.ShouldBeEmpty();
            GqlSemanticTree.Expression(rendered.GqlExpression.Projections[0].Expression).ShouldBe(GqlSemanticTree.Expression(expression));
        }
    }

    [Fact(DisplayName = "Cohesion Test [GQL] - Pattern comprehensions: the design summary links to the implemented contract (#1736)")]
    public void Documentation_PatternCollection_ShouldLinkToImplementedSection()
    {
        // Arrange / Act
        string design = File.ReadAllText(Path.Combine(TestDirectory(), "..", "docs", "DESIGN.md"));
        // Assert
        design.ShouldNotContain("Pattern comprehensions remain", Case.Sensitive);
        design.ShouldContain("[Pattern comprehensions](#pattern-comprehensions-1736-gq-c3)", Case.Sensitive);
        design.ShouldContain("### Pattern comprehensions (#1736, GQ-C3)", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Pattern comprehensions: collection property storage retains its capability refusal")]
    [InlineData("INSERT (n:N {k:[(a)->(b) | b.k]})")]
    [InlineData("MATCH (n) SET n.k=[(a)->(b) | b.k]")]
    public void Parse_StoredPatternList_ShouldRefuseComposite(string source)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(source);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Pattern comprehensions: excluded path prefixes keep the named capability refusal")]
    [InlineData("RETURN [p=WALK (a)->(b) | p]")]
    [InlineData("RETURN [WALK (a)->(b) | b]")]
    [InlineData("RETURN [ANY SHORTEST (a)->(b) | b]")]
    [InlineData("RETURN [p=ANY SHORTEST (a)->(b) | p]")]
    public void Parse_PathPrefix_ShouldRefuseUnsupportedForm(string source)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(source);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Pattern comprehensions: malformed bodies are syntax diagnostics")]
    [InlineData("RETURN [(a)->(b) WHERE b:A | (B|C)]", "CG00014")]
    [InlineData("RETURN [(a)->(b) | ]", "CG00002")]
    [InlineData("RETURN [p=() | p]", "CG00002")]
    [InlineData("RETURN [(a) | a]", "CG00002")]
    public void Parse_InvalidPatternCollection_ShouldRefuse(string source, string code)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(source);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    private static string TestDirectory([CallerFilePath] string source = "") => Path.GetDirectoryName(source)!;
}
