using System;
using System.ComponentModel;

using Assimalign.Cohesion.IdentityModel.Token;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

using Shouldly;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Tests;

/// <summary>
/// Contains unit tests for the JSON Web Token model.
/// </summary>
public sealed class JsonWebTokenTests
{
    [Fact]
    [DisplayName("Cohesion Test [IdentityModel.Token.JsonWebToken] - JsonWebToken: Should expose JWT metadata")]
    public void JsonWebToken_WhenConstructed_ShouldExposeJwtMetadata()
    {
        // Arrange
        const string rawToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ1c2VyLTQyIn0.signature";

        var descriptor = new JsonWebTokenDescriptor
        {
            Issuer = "https://issuer.cohesion.local",
            Subject = "user-42",
            RawData = rawToken
        };

        descriptor.Header.Add("alg", "HS256");
        descriptor.Header.Add("typ", "JWT");
        descriptor.Claims.Add(new IdentityTokenClaim("sub", "user-42"));

        // Act
        var token = new JsonWebToken(descriptor);

        // Assert
        token.Kind.ShouldBe(IdentityTokenKind.JsonWebToken);
        token.Algorithm.ShouldBe("HS256");
        token.TokenType.ShouldBe("JWT");
        token.Parts.ShouldNotBeNull();
        token.Parts.Header.ShouldBe("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9");
        token.Parts.Payload.ShouldBe("eyJzdWIiOiJ1c2VyLTQyIn0");
        token.Parts.Signature.ShouldBe("signature");
        token.TryGetHeaderValue("alg", out var algorithm).ShouldBeTrue();
        algorithm.ShouldBe("HS256");
    }

    [Fact]
    [DisplayName("Cohesion Test [IdentityModel.Token.JsonWebToken] - JsonWebTokenParts: Should parse three token segments")]
    public void JsonWebTokenParts_TryParse_WhenTokenHasThreeSegments_ShouldSucceed()
    {
        // Arrange
        const string rawToken = "header.payload.";

        // Act
        var result = JsonWebTokenParts.TryParse(rawToken, out var parts);

        // Assert
        result.ShouldBeTrue();
        parts.ShouldNotBeNull();
        parts.Header.ShouldBe("header");
        parts.Payload.ShouldBe("payload");
        parts.Signature.ShouldBe(string.Empty);
    }

    [Fact]
    [DisplayName("Cohesion Test [IdentityModel.Token.JsonWebToken] - JsonWebToken: Should reject malformed compact tokens")]
    public void JsonWebToken_WhenRawTokenIsMalformed_ShouldThrowArgumentException()
    {
        // Arrange
        var descriptor = new JsonWebTokenDescriptor
        {
            RawData = "header.payload"
        };

        // Act
        var exception = Should.Throw<ArgumentException>(() => new JsonWebToken(descriptor));

        // Assert
        exception.ParamName.ShouldBe("token");
    }
}
