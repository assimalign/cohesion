using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlUnionFixParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - UNION: NEXT has lower precedence than UNION")]
    [InlineData("RETURN 1 AS x UNION ALL RETURN 2 AS x NEXT RETURN x*10 AS x")]
    [InlineData("(RETURN 1 AS x) UNION ALL (RETURN 2 AS x) NEXT (RETURN x*10 AS x)")]
    [InlineData("RETURN 1 AS x UNION ALL RETURN 2 AS x NEXT RETURN x*10 AS x NEXT RETURN x+1 AS x")]
    [InlineData("(RETURN 1 AS x NEXT RETURN x+1 AS x) UNION ALL (RETURN 2 AS x NEXT RETURN x+2 AS x) NEXT RETURN x*10 AS x")]
    public void Parse_NextOverComposite_ShouldBuildPipeline(string source)
    {
        // Arrange / Act
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        parsed.GqlExpression.Composite.ShouldBeNull();
        parsed.GqlExpression.Stages.OfType<GqlNextStage>().ShouldNotBeEmpty();
        var rendered = parsed.GqlExpression.ToString();
        var roundtrip = (GqlQueryStatement)new GqlQueryParser().Parse(rendered);
        roundtrip.Diagnostics.ShouldBeEmpty();
        roundtrip.GqlExpression.ToString().ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - UNION: grouped capability scanning has relative query context")]
    [InlineData("MATCH p=ANY SHORTEST (n)-[:T]->(m) RETURN count(*) AS x", null)]
    [InlineData("MATCH p=ALL SHORTEST (n)-[:T]->(m) RETURN count(*) AS x", null)]
    [InlineData("MATCH p=SHORTEST 2 (n)-[:T]->(m) RETURN count(*) AS x", null)]
    [InlineData("MATCH WALK (n)-[:T]->{1,2}(m) RETURN count(*) AS x", null)]
    [InlineData("MATCH p=((n)-[:T]->(m)){1,2} RETURN count(*) AS x", null)]
    [InlineData("MATCH (n) RETURN {Order:n IS LABELED Order,x:all(v IN [1] WHERE v IS NOT NULL)} AS x", null)]
    [InlineData("MATCH (n:Order|Next) WHERE n IS LABELED Order RETURN count(*) AS x", null)]
    [InlineData("MATCH (n WHERE n.k IS NOT NULL) RETURN count(*) AS x", null)]
    [InlineData("MATCH (n) WHERE EXISTS { (n)->(m) WHERE m.k IS NOT NULL } RETURN count(*) AS x", null)]
    [InlineData("MATCH (n) RETURN [(n)->(m) | m.k] AS x", null)]
    [InlineData("FOR next IN [1,2] LET x=next FILTER x IS NOT NULL RETURN x", null)]
    [InlineData("MATCH (n) SET n IS Order REMOVE n IS Order RETURN count(*) AS x", null)]
    [InlineData("MATCH (n) OPTIONAL { MATCH (m) } RETURN count(*) AS x", "CK00001")]
    [InlineData("MATCH (n) WHERE n.k =~ 'a' RETURN count(*) AS x", "CK00001")]
    [InlineData("MATCH (n)~[r]~(m) RETURN count(*) AS x", "CK00001")]
    [InlineData("MATCH (n) RETURN mystery(n) AS x", "CG00010")]
    [InlineData("MATCH (n) RETURN [p=ANY (n)->(m) | m] AS x", "CK00001")]
    [InlineData("MATCH (n) RETURN $$x AS x", "CK00001")]
    [InlineData("SESSION SET GRAPH g", "CK00001")]
    [InlineData("MATCH (n) CALL proc() YIELD x RETURN x", "CK00001")]
    public void Parse_GroupedContext_ShouldMatchStandaloneDiagnostics(string operand, string? code)
    {
        // Arrange
        var parser = new GqlQueryParser();
        var standalone = parser.Parse(operand);
        // Act / Assert
        foreach (int depth in new[] { 1, 3 })
        {
            var grouped = parser.Parse("RETURN 0 AS x UNION ALL " + new string('(', depth) + operand + new string(')', depth));
            grouped.Diagnostics.Select(item => item.Code).ShouldBe(standalone.Diagnostics.Select(item => item.Code));
            if (code is null) { grouped.Diagnostics.ShouldBeEmpty(); }
            else { grouped.Diagnostics.ShouldContain(item => item.Code == code); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [GQL] - UNION DISTINCT: renders as UNION")]
    public void Parse_ExplicitDistinct_ShouldNormalizeToUnion()
    {
        // Arrange / Act
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse("RETURN 1 AS x UNION DISTINCT RETURN 2 AS x UNION RETURN 3 AS x");
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        var rendered = parsed.GqlExpression.Composite.ShouldNotBeNull().ToString();
        rendered.ShouldNotContain("DISTINCT", Case.Sensitive);
        new GqlQueryParser().Parse(rendered).Diagnostics.ShouldBeEmpty();
    }
}
