using System;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Contains unit tests for the normalized claim and its provenance.
/// </summary>
public sealed class IdentityClaimTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Claim: Constructor should snapshot all members")]
    public void Constructor_WhenConstructed_ShouldExposeAllMembers()
    {
        // Arrange
        var provenance = new IdentityClaimProvenance(
            AuthenticationProtocol.Saml2,
            originalType: "urn:oid:0.9.2342.19200300.100.1.3",
            originalIssuer: "https://idp.example",
            originalValueType: "xs:string",
            originalNameFormat: "urn:oasis:names:tc:SAML:2.0:attrname-format:uri",
            originalFriendlyName: "mail");

        // Act
        var claim = new IdentityClaim(IdentityClaimTypes.Email, "user@example.com", "https://idp.example", provenance);

        // Assert
        claim.Type.ShouldBe("email");
        claim.Value.AsString().ShouldBe("user@example.com");
        claim.Issuer.ShouldBe("https://idp.example");
        claim.Provenance.ShouldNotBeNull();
        claim.Provenance!.Protocol.ShouldBe(AuthenticationProtocol.Saml2);
        claim.Provenance.OriginalType.ShouldBe("urn:oid:0.9.2342.19200300.100.1.3");
        claim.Provenance.OriginalValueType.ShouldBe("xs:string");
        claim.Provenance.OriginalNameFormat.ShouldBe("urn:oasis:names:tc:SAML:2.0:attrname-format:uri");
        claim.Provenance.OriginalFriendlyName.ShouldBe("mail");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Claim: Constructor should reject invalid state")]
    public void Constructor_WhenGivenInvalidState_ShouldThrow()
    {
        Should.Throw<ArgumentException>(() => new IdentityClaim("", "value"));
        Should.Throw<ArgumentException>(() => new IdentityClaim("  ", "value"));

        // An undefined value is a construction bug, not data; Null is the explicit-null carrier.
        Should.Throw<ArgumentException>(() => new IdentityClaim("email", default));

        Should.NotThrow(() => new IdentityClaim("email", IdentityClaimValue.Null));
    }
}
