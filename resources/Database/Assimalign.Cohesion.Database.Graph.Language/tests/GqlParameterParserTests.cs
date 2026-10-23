using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlParameterParserTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Parameters: both sigils preserve bare names and complete spans")]
    [InlineData("$name", "name")]
    [InlineData("@name", "name")]
    [InlineData("$1", "1")]
    [InlineData("@01", "01")]
    [InlineData("$_x", "_x")]
    public void Parse_ParameterOperands_ShouldPreserveNamesAndLocations(string reference, string name)
    {
        // Arrange / Act
        foreach (string verb in new[] { "MATCH", "INSERT", "CREATE" })
        {
            string source = $"{verb} (a:P {{k: {reference}}})-[r:T {{k: {reference}}}]->(b) RETURN a";
            var statement = (GqlQueryStatement)new GqlQueryParser().Parse(source);

            // Assert
            statement.Diagnostics.ShouldBeEmpty();
            var path = (verb == "MATCH" ? statement.GqlExpression.Matches : statement.GqlExpression.Creates).Single();
            var node = path.Nodes[0].Properties["k"].ShouldBeOfType<GqlParameterExpression>();
            var edge = path.Relationships[0].Properties["k"].ShouldBeOfType<GqlParameterExpression>();
            node.Name.ShouldBe(name);
            node.Location!.Start.ShouldBe(source.IndexOf(reference, StringComparison.Ordinal));
            node.Location.End.ShouldBe(node.Location.Start + reference.Length);
            edge.Name.ShouldBe(name);
            edge.Location!.Start.ShouldBe(source.LastIndexOf(reference, StringComparison.Ordinal));
            edge.Location.End.ShouldBe(edge.Location.Start + reference.Length);
        }
        var predicate = ((GqlQueryStatement)new GqlQueryParser().Parse(
            $"MATCH (a) WHERE {reference} = {reference} RETURN a")).GqlExpression.Predicate.ShouldBeOfType<GqlBinaryExpression>();
        predicate.Left.ShouldBeOfType<GqlParameterExpression>().Name.ShouldBe(name);
        predicate.Right.ShouldBeOfType<GqlParameterExpression>().Name.ShouldBe(name);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Parameters: future forms keep named capability diagnostics")]
    [InlineData("MATCH (a) WHERE a.k = $$name RETURN a", "SUBSTITUTED PARAMETER REFERENCE")]
    [InlineData("MATCH (n $props) RETURN n", "WHOLE-MAP PARAMETER")]
    [InlineData("MATCH (n @props) RETURN n", "WHOLE-MAP PARAMETER")]
    [InlineData("MATCH (a)-[r $props]->(b) RETURN r", "WHOLE-MAP PARAMETER")]
    [InlineData("MATCH (a)-[r @props]->(b) RETURN r", "WHOLE-MAP PARAMETER")]
    [InlineData("INSERT (n $props)", "WHOLE-MAP PARAMETER")]
    public void Parse_UnsupportedPosition_ShouldReportCapability(string source, string form)
    {
        // Act
        var errors = new GqlQueryParser().Parse(source).Diagnostics;

        // Assert
        errors.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        errors.Single().Message!.ShouldContain(form, Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Parameters: malformed references and name positions are syntax errors")]
    [InlineData("MATCH (a) WHERE a.k = $ RETURN a")]
    [InlineData("MATCH (a) WHERE a.k = @ RETURN a")]
    [InlineData("MATCH (a) WHERE a.k = $1a RETURN a")]
    [InlineData("MATCH (a) WHERE a.k = @1a RETURN a")]
    [InlineData("MATCH (a) WHERE a.k = $١ RETURN a")]
    [InlineData("MATCH (a:$label) RETURN a")]
    [InlineData("MATCH (a)-[r:$type]->(b) RETURN a")]
    [InlineData("MATCH (a {$key: 1}) RETURN a")]
    [InlineData("MATCH ($variable) RETURN a")]
    [InlineData("MATCH (a) DELETE $a")]
    public void Parse_InvalidReference_ShouldReportSyntaxError(string source)
    {
        // Act / Assert
        new GqlQueryParser().Parse(source).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG00002");
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Parameters: malformed references precede capability errors at the sigil")]
    [InlineData("RETURN $")]
    [InlineData("RETURN @")]
    [InlineData("MATCH (a) RETURN $")]
    [InlineData("MATCH (a) RETURN @")]
    [InlineData("MATCH (n $) RETURN n")]
    [InlineData("MATCH (n @) RETURN n")]
    [InlineData("MATCH (a)-[r $]->(b) RETURN r")]
    [InlineData("MATCH (a)-[r @]->(b) RETURN r")]
    public void Parse_BareSigilInUnsupportedPosition_ShouldReportSyntaxAtSigil(string source)
    {
        // Arrange
        int sigil = source.IndexOfAny(['$', '@']);

        // Act
        var diagnostic = new GqlQueryParser().Parse(source).Diagnostics.ShouldHaveSingleItem();

        // Assert
        diagnostic.Code.ShouldBe("CG00002");
        diagnostic.Start.ShouldBe(sigil);
        diagnostic.End.ShouldBe(sigil + 1);
    }
}
