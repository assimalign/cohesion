using System;
using System.Text.Json;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlNumericLiteralTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Numeric literals retain digits for storage-domain validation by the engine")]
    [InlineData("1e-29", "1e-29")]
    [InlineData("1.00000000000000000000000000001", "1.00000000000000000000000000001")]
    [InlineData("1e100", "1e100")]
    [InlineData(".5", "0.5")]
    [InlineData("0001.25e2", "1.25e2")]
    public void Parse_NumericLiteral_ShouldPreserveDigitsAndSpan(string token, string json)
    {
        // Arrange
        string query = $"SELECT\n {token} FROM items";

        // Act
        var statement = new OqlQueryParser().Parse(query).ShouldBeOfType<OqlQueryStatement>();

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var literal = statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>().Projections[0].Expression.ShouldBeOfType<OqlLiteralExpression>();
        literal.Value.ShouldBeOfType<JsonElement>().GetRawText().ShouldBe(json);
        literal.Location!.Start.ShouldBe(query.IndexOf(token, StringComparison.Ordinal));
        literal.Location.End.ShouldBe(literal.Location.Start + token.Length);
        literal.Location.StartLine.ShouldBe(2);
    }
}
