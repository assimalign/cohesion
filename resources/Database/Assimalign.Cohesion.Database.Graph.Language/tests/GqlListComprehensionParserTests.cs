using System;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Graph.Language.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlListComprehensionParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - Comprehensions: optional operands preserve AST spans")]
    [InlineData("[x IN $xs]", false, false)]
    [InlineData("[x IN $xs WHERE x > 0]", true, false)]
    [InlineData("[x IN $xs | x + 1]", false, true)]
    [InlineData("[x IN $xs WHERE x > 0 | x + 1]", true, true)]
    [InlineData("[\"x y\" IN $xs | \"x y\"]", false, true)]
    [InlineData("[x IN $xs WHERE x:A | x.k]", true, true)]
    [InlineData("[x IN $xs WHERE x:A|B AND x:C|D | x]", true, true)]
    [InlineData("[x IN $xs WHERE x:(A|B)]", true, false)]
    [InlineData("[x IN $xs WHERE x:A | (B|C)]", true, false)]
    [InlineData("[x IN $xs WHERE x:A | x:B|C]", true, true)]
    [InlineData("[x IN $xs WHERE x<-1 | x]", true, true)]
    public void Parse_Comprehension_ShouldPreserveOperands(string value, bool predicate, bool projection)
    {
        // Arrange / Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN " + value);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var expression = statement.GqlExpression.Projections[0].Expression.ShouldBeOfType<GqlListComprehensionExpression>();
        expression.List.ShouldBeOfType<GqlParameterExpression>().Name.ShouldBe("xs");
        (expression.Predicate is not null).ShouldBe(predicate);
        (expression.Projection is not null).ShouldBe(projection);
        expression.Location!.Start.ShouldBe(7);
        expression.Location.End.ShouldBe(7 + value.Length);
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Slices: every open-ended form preserves absent bounds")]
    [InlineData("[1..2]", true, true)]
    [InlineData("[..2]", false, true)]
    [InlineData("[1..]", true, false)]
    [InlineData("[..]", false, false)]
    public void Parse_Slice_ShouldPreserveBounds(string suffix, bool from, bool to)
    {
        // Arrange / Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN $xs" + suffix);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var slice = statement.GqlExpression.Projections[0].Expression.ShouldBeOfType<GqlListSliceExpression>();
        (slice.From is not null).ShouldBe(from);
        (slice.To is not null).ShouldBe(to);
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Collection grammar: truncated and malformed operands are syntax errors")]
    [InlineData("RETURN [x IN ]")]
    [InlineData("RETURN [x IN [1] WHERE ]")]
    [InlineData("RETURN [x IN [1] | ]")]
    [InlineData("RETURN [x IN [1] | x WHERE TRUE]")]
    [InlineData("RETURN [1][..")]
    [InlineData("RETURN [1][1..2..3]")]
    public void Parse_InvalidCollection_ShouldReportSyntax(string source)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(source);
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG00002");
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - List predicates: admission remains recognized unsupported until GQ-C2")]
    [InlineData("all")]
    [InlineData("any")]
    [InlineData("none")]
    [InlineData("single")]
    public void Parse_ListPredicate_ShouldReportCapability(string name)
    {
        // Arrange / Act
        foreach (string source in new[] { $"RETURN {name}(x IN [1] WHERE x > 0)", $"RETURN 1, {name}(x IN [] WHERE NULL)", $"WITH {name}(x IN [] WHERE TRUE) AS p RETURN p" })
        {
            var statement = new GqlQueryParser().Parse(source);
            // Assert
            var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CK00001");
            diagnostic.Message.ShouldNotBeNull().ShouldContain(name.ToUpperInvariant(), Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [GQL] - Collections: path renderer round-trips lexical expressions")]
    public void Render_CollectionPredicate_ShouldPreserveSemanticTree()
    {
        // Arrange
        const string source = "MATCH (a WHERE size([x IN [1,2] WHERE x > 0 | [x IN [x] | x]][..1]) = 1)->(b) RETURN b";
        var first = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        first.Diagnostics.ShouldBeEmpty();
        // Act
        string rendered = first.GqlExpression.MatchClauses[0].Paths[0].ToString();
        var second = (GqlQueryStatement)new GqlQueryParser().Parse("MATCH " + rendered + " RETURN b");
        // Assert
        second.Diagnostics.ShouldBeEmpty();
        second.GqlExpression.MatchClauses[0].Paths[0].ToString().ShouldBe(rendered);
        GqlSemanticTree.Path(second.GqlExpression.MatchClauses[0].Paths[0]).ShouldBe(GqlSemanticTree.Path(first.GqlExpression.MatchClauses[0].Paths[0]));
    }
}
