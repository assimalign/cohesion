using System;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlDelimitedIdentifierTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Language] - Identifier: embedded double quotes parse and render reversibly")]
    [InlineData("a\"b")]
    [InlineData("\"")]
    [InlineData("a\"\"b")]
    [InlineData("space \" quote")]
    public void Parse_EmbeddedQuote_ShouldRoundTripLabel(string name)
    {
        // Arrange
        string quoted = "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var expression = new GqlLabelName(name);

        // Act
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse($"MATCH (n:{quoted}) RETURN n");
        string rendered = expression.ToString();
        var roundtrip = (GqlQueryStatement)new GqlQueryParser().Parse($"MATCH (n:{rendered}) RETURN n");

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        statement.GqlExpression.Matches.Single().Nodes.Single().Labels.ShouldBe([name]);
        rendered.ShouldBe(quoted);
        roundtrip.Diagnostics.ShouldBeEmpty();
        roundtrip.GqlExpression.Matches.Single().Nodes.Single().LabelExpression.ShouldBe(expression);
    }
}
