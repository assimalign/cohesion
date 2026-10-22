using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Tests;

public class JsonWebTokenPartsParsingTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityModel] - Compact parts: Should retain exact segments and signing input")]
    [InlineData("a.b.c", "a", "b", "c")]
    [InlineData("a.b.", "a", "b", "")]
    [InlineData(" a.b . c ", " a", "b ", " c ")]
    public void TryParse_ValidSegments_ShouldPreserveText(string compact, string header, string payload, string signature)
    {
        // Arrange / Act
        JsonWebTokenParts constructed = new(compact);
        bool parsed = JsonWebTokenParts.TryParse(compact, out JsonWebTokenParts? parts);

        // Assert
        parsed.ShouldBeTrue();
        parts.ShouldNotBeNull();
        parts.Header.ShouldBe(header);
        parts.Payload.ShouldBe(payload);
        parts.Signature.ShouldBe(signature);
        parts.SigningInput.ShouldBe(header + "." + payload);
        parts.ToString().ShouldBe(compact);
        constructed.ToString().ShouldBe(compact);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityModel] - Compact parts: Should reject invalid delimiters without throwing")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData(".b.c")]
    [InlineData("a..c")]
    public void TryParse_InvalidDelimiters_ShouldReturnFalse(string? compact)
    {
        // Arrange / Act
        bool parsed = JsonWebTokenParts.TryParse(compact!, out JsonWebTokenParts? parts);

        // Assert
        parsed.ShouldBeFalse();
        parts.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityModel] - Compact parts: Should reject whitespace-only encoded segments without throwing")]
    [InlineData(" .b.c")]
    [InlineData("a. .c")]
    [InlineData("\t.b.")]
    public void TryParse_WhitespaceSegment_ShouldReturnFalse(string compact)
    {
        // Arrange / Act
        bool parsed = JsonWebTokenParts.TryParse(compact, out JsonWebTokenParts? parts);

        // Assert
        parsed.ShouldBeFalse();
        parts.ShouldBeNull();
    }
}
