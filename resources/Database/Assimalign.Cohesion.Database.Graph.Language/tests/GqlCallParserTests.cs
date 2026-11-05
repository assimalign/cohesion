using System;
using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlCallParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - CALL scopes and nested composites retain semantic AST and render")]
    [InlineData("CALL { RETURN 1 AS y } RETURN y", false, false, -1)]
    [InlineData("CALL () { RETURN 1 AS y } RETURN y", false, false, 0)]
    [InlineData("LET x=1 CALL (x) { RETURN x+1 AS y } RETURN y", false, false, 1)]
    [InlineData("LET x=1 CALL (*) { RETURN x+1 AS y } RETURN y", false, true, 0)]
    [InlineData("OPTIONAL CALL () { RETURN 1 AS y } RETURN y", true, false, 0)]
    [InlineData("LET a=1,b=2 CALL (a,b) { (RETURN a AS y UNION RETURN b AS y) NEXT RETURN y } RETURN y", false, false, 2)]
    [InlineData("CALL { CALL { RETURN {call:1}.call AS z } RETURN z AS y } RETURN y", false, false, -1)]
    public void Parse_InlineCall_ShouldPreserveScopeAndRoundTrip(string source, bool optional, bool all, int imports)
    {
        // Arrange / Act
        var parsed = (GqlQueryStatement)new GqlQueryParser().Parse(source);
        // Assert
        parsed.Diagnostics.ShouldBeEmpty();
        var call = parsed.GqlExpression.Stages.OfType<GqlCallStage>().ShouldHaveSingleItem();
        call.Optional.ShouldBe(optional); call.ImportAll.ShouldBe(all); (call.Imports?.Count ?? -1).ShouldBe(imports);
        call.Location!.Start.ShouldBe(source.IndexOf(optional ? "OPTIONAL" : "CALL", StringComparison.Ordinal));
        var rendered = parsed.GqlExpression.ToString();
        var again = (GqlQueryStatement)new GqlQueryParser().Parse(rendered);
        again.Diagnostics.ShouldBeEmpty();
        again.GqlExpression.ToString().ShouldBe(rendered);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - CALL named procedures, batching and nested capabilities stay refused")]
    [InlineData("CALL db.labels() YIELD label RETURN label")]
    [InlineData("OPTIONAL CALL proc() YIELD x RETURN x")]
    [InlineData("CALL () { RETURN 1 AS x } IN TRANSACTIONS RETURN x")]
    [InlineData("CALL { RETURN 1 AS x UNION (CALL named()) } RETURN x")]
    [InlineData("CALL { MATCH (n) FOREACH (x IN [1] | SET n.x=x) RETURN 1 AS y } RETURN y")]
    [InlineData("CALL { RETURN 1 AS x YIELD x } RETURN x")]
    public void Parse_ExcludedCall_ShouldReportCapability(string source)
    {
        // Arrange / Act
        var parsed = new GqlQueryParser().Parse(source);
        // Assert
        parsed.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - malformed CALL syntax is located")]
    [InlineData("CALL (x,) { RETURN 1 AS y } RETURN y")]
    [InlineData("CALL (*) RETURN 1 AS y")]
    [InlineData("CALL (x { RETURN 1 AS y } RETURN y")]
    [InlineData("CALL { RETURN 1 AS y RETURN y")]
    public void Parse_MalformedCall_ShouldReportSyntax(string source)
    {
        // Arrange / Act
        var parsed = new GqlQueryParser().Parse(source);
        // Assert
        parsed.Diagnostics.ShouldNotBeEmpty();
        parsed.Diagnostics.All(diagnostic => diagnostic.Start is not null && diagnostic.Code == "CG00002").ShouldBeTrue(
            string.Join("; ", parsed.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message)));
    }
}
