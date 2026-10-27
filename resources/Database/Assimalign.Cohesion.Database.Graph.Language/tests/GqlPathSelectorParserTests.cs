using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Language.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlPathSelectorParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Selector corpus preserves kinds, counts, modes and prefix spans (#1734)")]
    [InlineData("ANY", GqlPathSelectorKind.Any, 1L)]
    [InlineData("ALL", GqlPathSelectorKind.All, 1L)]
    [InlineData("ANY SHORTEST", GqlPathSelectorKind.AnyShortest, 1L)]
    [InlineData("ALL SHORTEST", GqlPathSelectorKind.AllShortest, 1L)]
    [InlineData("SHORTEST 2", GqlPathSelectorKind.Shortest, 2L)]
    [InlineData("SHORTEST 9223372036854775807", GqlPathSelectorKind.Shortest, long.MaxValue)]
    [InlineData("SHORTEST 2", GqlPathSelectorKind.ShortestGroups, 2L)]
    public void Parse_SelectorCorpus_ShouldPreserveSemantics(string head, GqlPathSelectorKind kind, long count)
    {
        // Arrange / Act / Assert
        foreach (string mode in new[] { "", "WALK", "TRAIL", "SIMPLE", "ACYCLIC" })
        {
            foreach (string pathToken in new[] { "", "PATH", "PATHS" })
            {
                string prefix = string.Join(" ", new[] { head, mode, pathToken,
                    kind == GqlPathSelectorKind.ShortestGroups ? "GROUPS" : "" }.Where(word => word.Length != 0));
                string source = $"MATCH p = {prefix} ((a:V {{k:$min}} WHERE a.k > 0)-[r:T WHERE r.w > 0]->{{0,3}}(b) WHERE a.k <> b.k) RETURN p";
                var statement = (GqlQueryStatement)new GqlQueryParser().Parse(source);
                statement.Diagnostics.ShouldBeEmpty();
                var path = statement.GqlExpression.Matches.ShouldHaveSingleItem();
                path.Variable.ShouldBe("p");
                path.Selector.ShouldNotBeNull().Kind.ShouldBe(kind);
                path.Selector.Count.ShouldBe(count);
                path.Mode.ShouldBe(mode.Length == 0 ? null : Enum.Parse<GqlPathMode>(mode, true));
                var location = path.Selector.Location.ShouldNotBeNull();
                source[location.Start..location.End].ShouldBe(prefix);
                var roundtrip = (GqlQueryStatement)new GqlQueryParser().Parse($"MATCH {path} RETURN p");
                roundtrip.Diagnostics.ShouldBeEmpty();
                GqlSemanticTree.Path(roundtrip.GqlExpression.Matches[0]).ShouldBe(GqlSemanticTree.Path(path));
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Counted ANY parameters retain the complete unsupported prefix (#1734)")]
    [InlineData("ANY $k")]
    [InlineData("ANY @k TRAIL PATHS")]
    [InlineData("ANY 2 WALK PATH")]
    public void Parse_CountedAny_ShouldReportCapabilityAtPrefix(string prefix)
    {
        // Arrange
        string source = $"MATCH {prefix} (a)->(b) RETURN a";
        // Act
        var diagnostic = new GqlQueryParser().Parse(source).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CK00001");
        source[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe(prefix);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Complete paths render variables, predicates, directions and quantifiers (#1734)")]
    [InlineData("p = SHORTEST 2 WALK GROUPS (a WHERE a.k > 0)->{0,3}(b)")]
    [InlineData("p = SHORTEST 1 TRAIL PATH GROUP (a) ((x WHERE x.k>0)-[r:T WHERE r.w>0]->(y) WHERE x.k<>y.k){0,3} (b)")]
    [InlineData("p = ANY (\"odd\"\"name\":A|!B {\"odd key\":'a\\nb''c',d:1.5} WHERE \"odd\"\"name\".k <> $min)<-[r:!T WHERE r.w IS NOT NULL]-(b)")]
    [InlineData("p = ALL SIMPLE (a)<->+(b)")]
    [InlineData("p = ANY SHORTEST ACYCLIC (a)-*(b)")]
    [InlineData("p = ALL SHORTEST (a)-[r:T*0]->(b)")]
    [InlineData("p = WALK (a)->{2}(b)")]
    [InlineData("p = (a WHERE (a.k>0 AND a.k<3) OR NOT a:Bad)->(b)")]
    [InlineData("p = ANY (a WHERE CASE a.k WHEN > 1 THEN coalesce(a.k,2) ELSE nullif(a.k,0) END > 0)")]
    [InlineData("p = ALL (a WHERE {k:[a.k,2]}['k'][0] IN [1,2] AND upper('a') STARTS WITH 'A')")]
    [InlineData("p = ANY (a WHERE CAST(a.k AS DOUBLE PRECISION) > -1.25 AND CURRENT_DATE IS NOT NULL AND (a.k=1) IS TRUE)")]
    [InlineData("p = ANY (a WHERE log(2,a.k)>0 AND a IS NOT LABELED Bad)")]
    [InlineData("p = ANY (a WHERE a.k=1.0 OR a.k=-0.0)")]
    public void ToString_CompletePath_ShouldRoundTripSemanticTree(string pattern)
    {
        // Arrange
        var original = (GqlQueryStatement)new GqlQueryParser().Parse($"MATCH {pattern} RETURN p");
        original.Diagnostics.ShouldBeEmpty();
        var path = original.GqlExpression.Matches.ShouldHaveSingleItem();
        // Act
        var reparsed = (GqlQueryStatement)new GqlQueryParser().Parse($"MATCH {path} RETURN p");
        // Assert: compare all public semantic fields, independently of the renderer; locations and source text are normalized away.
        reparsed.Diagnostics.ShouldBeEmpty();
        GqlSemanticTree.Path(reparsed.GqlExpression.Matches.ShouldHaveSingleItem()).ShouldBe(GqlSemanticTree.Path(path));
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Selector count failures are located CG00011 (#1734)")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1.5")]
    [InlineData("$k")]
    [InlineData("9223372036854775808")]
    [InlineData("")]
    public void Parse_InvalidCount_ShouldRefuseBeforeBinding(string count)
    {
        // Arrange
        string source = $"MATCH SHORTEST {count} (a)->(b) RETURN a";
        // Act
        var statement = new GqlQueryParser().Parse(source);
        // Assert
        var diagnostic = statement.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CG00011");
        diagnostic.Start.ShouldNotBeNull();
        diagnostic.End.ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Malformed selector combinations remain syntax errors (#1734)")]
    [InlineData("ANY ALL")]
    [InlineData("ALL 2")]
    [InlineData("ANY SHORTEST 2")]
    [InlineData("SHORTEST 2 GROUPS WALK")]
    [InlineData("ANY TRAIL WALK")]
    public void Parse_InvalidPrefix_ShouldReportSyntax(string prefix)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse($"MATCH {prefix} (a)->(b) RETURN a");
        // Assert
        statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG00002");
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Nested selector refusals retain the complete prefix span (#1734)")]
    [InlineData("SHORTEST 2 TRAIL PATHS GROUPS")]
    [InlineData("ANY SHORTEST WALK PATH")]
    public void Parse_NestedSelector_ShouldRefuseAtCompletePrefix(string prefix)
    {
        // Arrange
        string source = $"MATCH ({prefix} (a)->(b)) RETURN a";
        // Act
        var error = new GqlQueryParser().Parse(source).Diagnostics.ShouldHaveSingleItem();
        // Assert
        error.Code.ShouldBe("CK00001");
        source[error.Start!.Value..error.End!.Value].ShouldBe(prefix);
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Language] - Prefilter AST distinguishes element, path and post-selection predicates (#1734)")]
    public void Parse_Prefilters_ShouldKeepTheirOwners()
    {
        // Arrange
        const string source = "MATCH p = ANY SHORTEST ((a WHERE a.k > $min)-[r:T WHERE r.w > 0]->(b) WHERE a.k <> b.k) WHERE path_length(p) > 1 RETURN p";
        // Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var path = statement.GqlExpression.Matches.ShouldHaveSingleItem();
        var inner = path.Elements.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBeOfType<GqlParenthesizedPathPattern>().Pattern;
        inner.Nodes[0].Predicate.ShouldBeOfType<GqlBinaryExpression>();
        inner.Relationships[0].Predicate.ShouldBeOfType<GqlBinaryExpression>();
        inner.Predicate.ShouldBeOfType<GqlBinaryExpression>();
        statement.GqlExpression.Predicate.ShouldBeOfType<GqlBinaryExpression>();
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Selectors plan unbounded WALK, quantified bodies and zero-hop paths (#1734)")]
    [InlineData("ANY SHORTEST WALK (a)->*(b)")]
    [InlineData("ALL (a)-[r:T*0]->(b)")]
    [InlineData("SHORTEST 2 ACYCLIC GROUPS (a) ((x WHERE x.k > 0)-[r:T WHERE r.w > 0]->(y) WHERE x.k <> y.k){0,3} (b)")]
    [InlineData("ANY (a)")]
    [InlineData("SHORTEST 1 TRAIL PATH GROUP (a)->(b)")]
    [InlineData("ALL SHORTEST (a)->(a), ANY (a)<-(b)")]
    public void Parse_PatternIntegration_ShouldRetainSelector(string pattern)
    {
        // Arrange / Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse($"MATCH {pattern} RETURN a");
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.Matches[0].Selector.ShouldNotBeNull();
    }
}
