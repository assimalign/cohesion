using System;
using System.ComponentModel;

using Assimalign.Cohesion.IdentityModel.Token;

using Shouldly;

namespace Assimalign.Cohesion.IdentityModel.Token.Tests;

/// <summary>
/// Contains unit tests for the shared identity token model.
/// </summary>
public sealed class IdentityTokenTests
{
    [Fact]
    [DisplayName("Cohesion Test [IdentityModel.Token] - IdentityToken: Should snapshot descriptor values")]
    public void IdentityToken_WhenConstructed_ShouldSnapshotDescriptorValues()
    {
        // Arrange
        var issuedAt = new DateTimeOffset(2026, 3, 18, 12, 0, 0, TimeSpan.Zero);
        var descriptor = new IdentityTokenDescriptor
        {
            Id = "token-001",
            Subject = "user-42",
            Issuer = "https://issuer.cohesion.local",
            TokenType = "Bearer",
            RawData = "raw-token-value",
            IssuedAt = issuedAt,
            NotBefore = issuedAt.AddMinutes(-5),
            ExpiresAt = issuedAt.AddMinutes(55)
        };

        descriptor.Audiences.Add("api://orders");
        descriptor.Claims.Add(new IdentityTokenClaim("sub", "user-42"));
        descriptor.Properties.Add("tenant", "cohesion");

        // Act
        var token = new TestIdentityToken(IdentityTokenKind.JsonWebToken, descriptor);

        descriptor.Audiences.Add("api://billing");
        descriptor.Claims.Add(new IdentityTokenClaim("role", "reader"));
        descriptor.Properties["tenant"] = "updated";

        // Assert
        token.Kind.ShouldBe(IdentityTokenKind.JsonWebToken);
        token.Id.ShouldBe("token-001");
        token.Subject.ShouldBe("user-42");
        token.Issuer.ShouldBe("https://issuer.cohesion.local");
        token.TokenType.ShouldBe("Bearer");
        token.RawData.ShouldBe("raw-token-value");
        token.IssuedAt.ShouldBe(issuedAt);
        token.Audiences.Count.ShouldBe(1);
        token.Audiences[0].ShouldBe("api://orders");
        token.Claims.Count.ShouldBe(1);
        token.Claims[0].Type.ShouldBe("sub");
        token.Properties["tenant"].ShouldBe("cohesion");
    }

    [Fact]
    [DisplayName("Cohesion Test [IdentityModel.Token] - IdentityToken: Should resolve claims by type")]
    public void IdentityToken_TryGetClaim_WhenClaimExists_ShouldReturnClaim()
    {
        // Arrange
        var descriptor = new IdentityTokenDescriptor();
        descriptor.Claims.Add(new IdentityTokenClaim("sub", "user-42"));
        descriptor.Claims.Add(new IdentityTokenClaim("role", "reader"));
        descriptor.Claims.Add(new IdentityTokenClaim("role", "writer"));

        var token = new TestIdentityToken(IdentityTokenKind.Saml, descriptor);

        // Act
        var hasClaim = token.TryGetClaim("role", out var claim);
        var claims = token.GetClaims("role");

        // Assert
        hasClaim.ShouldBeTrue();
        claim.ShouldNotBeNull();
        claim.Type.ShouldBe("role");
        claims.Count.ShouldBe(2);
        token.HasAudience("api://orders").ShouldBeFalse();
    }

    private sealed class TestIdentityToken : IdentityToken
    {
        public TestIdentityToken(IdentityTokenKind kind, IdentityTokenDescriptor descriptor)
            : base(kind, descriptor)
        {
        }
    }
}
