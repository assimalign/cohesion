using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlWindowCallBoundaryTests
{
    [Theory(DisplayName = "Cohesion Test [OQL] - Call arguments named over preserve paths and reserved-name fields (#1805)")]
    [InlineData("SELECT MAX(over.rank) AS best FROM c UNNEST COALESCE(c.items, []) over")]
    [InlineData("SELECT COALESCE(over, c.rank) AS v FROM c UNNEST COALESCE(c.a, []) over")]
    [InlineData("SELECT ABS(over.rank) AS v, ROW_NUMBER() OVER (ORDER BY c.k) AS n FROM c UNNEST c.items AS over")]
    [InlineData("SELECT COALESCE(over, first_value) AS v FROM c UNNEST COALESCE(c.a, []) over")]
    public void Parse_CallArgumentName_ShouldPreserveExpression(string text)
    {
        // Arrange
        var parser = new OqlQueryParser();
        // Act
        var statement = parser.Parse(text);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [OQL] - Null modifier words inside calls retain the base syntax diagnostic (#1805)")]
    [InlineData("respect", "")]
    [InlineData("ignore", "")]
    [InlineData("respect", " UNNEST COALESCE(c.a, []) over")]
    [InlineData("ignore", " UNNEST COALESCE(c.a, []) over")]
    public void Parse_NullModifierArgument_ShouldKeepSyntaxDiagnostic(string word, string tail)
    {
        // Arrange
        string text = $"SELECT f({word} nulls) FROM c{tail}";
        var parser = new OqlQueryParser(true);
        // Act
        var diagnostic = parser.Parse(text).Diagnostics.ShouldHaveSingleItem();
        // Assert
        diagnostic.Code.ShouldBe("CD00002");
        diagnostic.Start.ShouldBe(text.IndexOf("nulls", StringComparison.Ordinal));
        diagnostic.Message.ShouldBe("Expected ')'.");
    }
}
