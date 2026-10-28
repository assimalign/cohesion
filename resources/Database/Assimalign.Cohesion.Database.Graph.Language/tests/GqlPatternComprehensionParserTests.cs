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
}
