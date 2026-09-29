using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

/// <summary>Checks the named MATCH path grammar independently of graph execution.</summary>
public sealed class GqlPathBindingParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Named paths bind the complete MATCH pattern")]
    [InlineData("MATCH p = (a)-[r:LINK]->(b) RETURN p", "p")]
    [InlineData("MATCH \"my path\" = (a)<-[r:LINK]-(b) RETURN \"my path\" AS result", "my path")]
    public void Parse_NamedMatchPath_PreservesPathVariableAndPattern(string statement, string variable)
    {
        var parsed = new GqlQueryParser().Parse(statement).ShouldBeOfType<GqlQueryStatement>();

        parsed.Diagnostics.ShouldBeEmpty();
        var path = parsed.GqlExpression.Matches.Single();
        path.Variable.ShouldBe(variable);
        path.Nodes.Select(node => node.Variable).ShouldBe(["a", "b"]);
        path.Relationships.Single().Variable.ShouldBe("r");
        parsed.GqlExpression.Projections.Single().Variable.ShouldBe(variable);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Invalid path binding syntax is rejected")]
    [InlineData("MATCH p (a) RETURN p")]
    [InlineData("MATCH p = RETURN p")]
    [InlineData("MATCH = (a) RETURN a")]
    [InlineData("CREATE p = (a) RETURN p")]
    [InlineData("INSERT p = (a) RETURN p")]
    public void Parse_InvalidPathBinding_ReportsSyntaxDiagnostic(string statement)
    {
        var parsed = new GqlQueryParser().Parse(statement);

        parsed.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "GQL0002");
    }
}
