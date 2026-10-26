using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlPathModeParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Path modes preserve explicitness and apply at the outer path (#1733)")]
    [InlineData(GqlPathMode.Walk)]
    [InlineData(GqlPathMode.Trail)]
    [InlineData(GqlPathMode.Simple)]
    [InlineData(GqlPathMode.Acyclic)]
    public void Parse_OuterMode_ShouldRetainModeAndPathVariable(GqlPathMode mode)
    {
        // Arrange / Act / Assert
        foreach (string token in new[] { "", " PATH", " PATHS" })
        {
            foreach (string body in new[] { "(a)->{1,3}(b)", "((a)->(b)){1,3}", "((a)->(b))", "(a)", "(a)-[*0]->(b)" })
            {
                var statement = (GqlQueryStatement)new GqlQueryParser().Parse($"OPTIONAL MATCH p = {mode.ToString().ToLowerInvariant()}{token} {body} RETURN p");
                statement.Diagnostics.ShouldBeEmpty();
                var path = statement.GqlExpression.MatchClauses[0].Paths.ShouldHaveSingleItem();
                path.Mode.ShouldBe(mode);
                path.Variable.ShouldBe("p");
                foreach (var inner in path.Elements!.OfType<GqlParenthesizedPathPattern>())
                { inner.Pattern.Mode.ShouldBeNull(); }
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Path modes are contextual and local to each comma-separated path (#1733)")]
    public void Parse_SeveralPaths_ShouldPreserveIndependentModes()
    {
        // Arrange / Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse(
            "MATCH trail = WALK (walk)-[simple]->{1,2}(acyclic), SIMPLE (x)->(y), (z) RETURN trail");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.Matches.Select(path => path.Mode).ShouldBe(new GqlPathMode?[] { GqlPathMode.Walk, GqlPathMode.Simple, null });
        statement.GqlExpression.Matches[0].Variable.ShouldBe("trail");
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Unbounded WALK is a capability refusal at its prefix (#1733)")]
    [InlineData("(a)->+(b)")]
    [InlineData("(a)->*(b)")]
    [InlineData("(a)->{2,}(b)")]
    [InlineData("(a)->{,}(b)")]
    [InlineData("(a)-[*]->(b)")]
    [InlineData("(a)-[*2..]->(b)")]
    [InlineData("((a)->(b))+")]
    [InlineData("(a) (((x)->+(y))) (b)")]
    public void Parse_UnboundedWalk_ShouldReportCapabilityDiagnostic(string body)
    {
        // Arrange
        string source = "MATCH p = WALK PATHS " + body + " RETURN p";
        // Act
        var diagnostic = new GqlQueryParser().Parse(source).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CK00001");
        diagnostic.Message.ShouldNotBeNull().ShouldContain("UNBOUNDED WALK", Case.Sensitive);
        source[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe("WALK PATHS");
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Every nested explicit mode remains refused, with or without quantification (#1733)")]
    [InlineData(GqlPathMode.Walk)]
    [InlineData(GqlPathMode.Trail)]
    [InlineData(GqlPathMode.Simple)]
    [InlineData(GqlPathMode.Acyclic)]
    public void Parse_NestedMode_ShouldReportCapabilityDiagnostic(GqlPathMode mode)
    {
        // Arrange / Act / Assert
        foreach (string suffix in new[] { "", "{1,2}" })
        {
            string source = $"MATCH WALK (({mode.ToString().ToUpperInvariant()} PATH (a)->(b)){suffix}) RETURN a";
            var diagnostic = new GqlQueryParser().Parse(source).Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CK00001");
            diagnostic.Start.ShouldBe(source.IndexOf(mode.ToString(), 10, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Path modes cannot decorate insertions or malformed paths (#1733)")]
    [InlineData("INSERT WALK (a)->(b)")]
    [InlineData("MATCH WALK TRAIL (a)->(b) RETURN a")]
    [InlineData("MATCH WALK PATHS RETURN 1")]
    public void Parse_InvalidModePosition_ShouldReportSyntaxDiagnostic(string source)
    {
        // Arrange / Act / Assert
        new GqlQueryParser().Parse(source).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG00002");
    }
}
