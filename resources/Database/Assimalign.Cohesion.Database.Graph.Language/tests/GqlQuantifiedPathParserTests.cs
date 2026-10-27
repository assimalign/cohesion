using System;
using System.Linq;
using System.Threading;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlQuantifiedPathParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Quantifiers: ISO and Cypher bounds lower into the same AST")]
    [InlineData("-[r:T]->{2}", 2, 2)]
    [InlineData("-[r:T]->{1,3}", 1, 3)]
    [InlineData("-[r:T]->{2,}", 2, null)]
    [InlineData("-[r:T]->{,3}", 0, 3)]
    [InlineData("->+", 1, null)]
    [InlineData("->*", 0, null)]
    [InlineData("->{,}", 0, null)]
    [InlineData("<-[:T]-{1,2}", 1, 2)]
    [InlineData("-[:T]-+", 1, null)]
    [InlineData("->{1,3}", 1, 3)]
    [InlineData("-{2}", 2, 2)]
    [InlineData("<->{,3}", 0, 3)]
    [InlineData("-[r:T*]->", 1, null)]
    [InlineData("-[*2]->", 2, 2)]
    [InlineData("-[*1..3]->", 1, 3)]
    [InlineData("-[*..3]->", 1, 3)]
    [InlineData("-[*2..]->", 2, null)]
    [InlineData("-[*0..2]->", 0, 2)]
    [InlineData("-[*0]->", 0, 0)]
    [InlineData("-[r]->*", 0, null)]
    [InlineData("-[r:T*1..3 {w: 1}]->", 1, 3)]
    public void Parse_RelationshipQuantifier_ShouldPreserveBounds(string edge, int min, int? max)
    {
        // Arrange / Act
        var statement = Parse("MATCH (a)" + edge + "(b) RETURN a");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var path = statement.GqlExpression.MatchClauses[0].Paths.ShouldHaveSingleItem();
        path.Nodes.Count.ShouldBe(2);
        path.Relationships.ShouldHaveSingleItem().Quantifier.ShouldBe(new GqlQuantifier(min, max));
        path.Elements!.Count.ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Quantified paths: parenthesized elements preserve source order and star bindings")]
    [InlineData("MATCH (a) ((x)-[:T]->(y)){1,3} (b) RETURN *")]
    [InlineData("MATCH ((x)-[r:T]->(y)){2} RETURN *")]
    [InlineData("MATCH ((x)-[]->(y))* RETURN *")]
    [InlineData("MATCH (a) (((x)-[]->(y))){2} (b) RETURN a")]
    public void Parse_ParenthesizedPath_ShouldRetainElements(string source)
    {
        // Act
        var statement = Parse(source);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var path = statement.GqlExpression.MatchClauses[0].Paths.ShouldHaveSingleItem();
        path.Nodes.ShouldBeEmpty();
        path.Relationships.ShouldBeEmpty();
        path.Elements!.OfType<GqlParenthesizedPathPattern>().ShouldHaveSingleItem().Quantifier.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Quantifiers: malformed bounds keep stable codes and valid spans")]
    [InlineData("MATCH (a)->{3,1}(b) RETURN a", "CG00002")]
    [InlineData("MATCH (a)->{0}(b) RETURN a", "CG00002")]
    [InlineData("MATCH (a)->{0,0}(b) RETURN a", "CG00002")]
    [InlineData("MATCH (a)(b) RETURN a", "CG00002")]
    [InlineData("MATCH (a)->{1,65}(b) RETURN a", "CG00005")]
    [InlineData("MATCH (a)->{65,}(b) RETURN a", "CG00005")]
    [InlineData("MATCH (a)->{1,40}(b)->{1,40}(c) RETURN a", "CG00005")]
    [InlineData("MATCH (a) ((x)->(y)->(z)){33} (b) RETURN a", "CG00005")]
    [InlineData("MATCH (a)->{1,999999999999999999999}(b) RETURN a", "CG00004")]
    [InlineData("MATCH (a)-[*999999999999999999999]->(b) RETURN a", "CG00004")]
    [InlineData("MATCH (a){1,3} RETURN a", "CG00002")]
    [InlineData("MATCH (a)+ RETURN a", "CG00002")]
    [InlineData("INSERT (a)->{1,3}(b)", "CG00002")]
    [InlineData("CREATE (a)-[*2]->(b)", "CG00002")]
    [InlineData("INSERT ((a)->(b)){2}", "CG00002")]
    [InlineData("MATCH (a) RETURN a.x ?", "CG00002")]
    public void Parse_InvalidQuantifier_ShouldReportOneDiagnostic(string source, string code)
    {
        // Act
        var error = Parse(source).Diagnostics.ShouldHaveSingleItem();
        // Assert
        error.Code.ShouldBe(code);
        error.Start.ShouldNotBeNull().ShouldBeInRange(0, source.Length);
        error.End.ShouldNotBeNull().ShouldBeInRange(error.Start.Value, source.Length);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Quantifiers: owned exclusions report one capability diagnostic")]
    [InlineData("MATCH (p = (a)-[]->(b)){1,3} RETURN p", "SUBPATH VARIABLE", "p =")]
    [InlineData("MATCH ((a)-[]->(b))? RETURN a", "QUESTIONED PATH", "?")]
    [InlineData("MATCH (a)-[]->?(b) RETURN a", "QUESTIONED PATH", "?")]
    [InlineData("MATCH ((a)->{2}(b)){2} RETURN a", "NESTED QUANTIFIER", "{2} RETURN")]
    [InlineData("MATCH (a)->+{2}(b) RETURN a", "NESTED QUANTIFIER", "{2}")]
    [InlineData("MATCH (a)->{2}?(b) RETURN a", "NESTED QUANTIFIER", "?")]
    [InlineData("MATCH (TRAIL (a)->(b)){1,3} RETURN a", "TRAIL", "TRAIL")]
    [InlineData("MATCH (ANY SHORTEST (a)->(b)){1,3} RETURN a", "ANY SHORTEST", "ANY")]
    [InlineData("MATCH ((a)->(b)) (WALK (c)->(d)){2} RETURN a", "WALK", "WALK")]
    [InlineData("MATCH (a) ((x)~[:T]~(y)){1,3} (b) RETURN a", "UNDIRECTED EDGE", "~")]
    [InlineData("MATCH WALK (a)-[:T]->+(b) RETURN a", "WALK", "WALK")]
    public void Parse_UnsupportedQuantifierConstruct_ShouldNameFirstToken(string source, string construct, string first)
    {
        // Act
        var error = Parse(source).Diagnostics.ShouldHaveSingleItem();
        // Assert
        error.Code.ShouldBe("CK00001");
        error.Message.ShouldNotBeNull().ShouldContain(construct, Case.Sensitive);
        error.Start.ShouldBe(source.IndexOf(first, StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Quantifiers: adjacent dash comments cannot truncate a quantified path")]
    [InlineData("MATCH (a)->{2}--\n>(b) RETURN a")]
    [InlineData("MATCH (a:V {k:1})-[:T*--1..1]->(hidden)\n]->(b) RETURN b.k")]
    [InlineData("MATCH (a)-[:T*1--..2\n]->(b) RETURN a")]
    [InlineData("MATCH (a)->+--\n>(b) RETURN a")]
    [InlineData("MATCH (a)->*--\n>(b) RETURN a")]
    [InlineData("MATCH ((a)->(b))--\n>(c) RETURN a")]
    [InlineData("MATCH (a) ((x)-->(y)){1,3} (b) RETURN a")]
    public void Parse_CommentAfterQuantifier_ShouldReportArrowDiagnostic(string source)
    {
        // Act
        var error = Parse(source).Diagnostics.ShouldHaveSingleItem();
        // Assert
        error.Code.ShouldBe("CG00008");
        error.Start.ShouldBe(source.IndexOf("--", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Quantified paths: recursive subpath parsing observes the small-thread stack guard")]
    public void Parse_DeepSubpath_ShouldReportStackGuard()
    {
        // Arrange
        string source = "MATCH " + new string('(', 100_000) + "(a)->(b)" + new string(')', 100_000) + " RETURN a";
        GqlQueryStatement? statement = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { statement = Parse(source); }
            catch (Exception error) { failure = error; }
        }, 512 * 1024);
        // Act
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        // Assert
        failure.ShouldBeNull();
        statement.ShouldNotBeNull().Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG00009");
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Quantified paths: an oversized body cannot hide its bound behind a zero-minimum quantifier")]
    public void Parse_OversizedParenthesizedBody_ShouldReportBoundDiagnostic()
    {
        // Arrange
        string body = "(x)" + string.Concat(Enumerable.Repeat("->()", 65));
        // Act / Assert
        foreach (string suffix in new[] { "*", "{0,1}", "+" })
        { Parse("MATCH (a) (" + body + ")" + suffix + " (b) RETURN a").Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG00005"); }
    }

    private static GqlQueryStatement Parse(string source) => (GqlQueryStatement)new GqlQueryParser().Parse(source);
}
