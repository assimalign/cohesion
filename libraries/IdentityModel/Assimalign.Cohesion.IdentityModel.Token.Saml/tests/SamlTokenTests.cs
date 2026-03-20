using System.ComponentModel;

using Assimalign.Cohesion.IdentityModel.Token;
using Assimalign.Cohesion.IdentityModel.Token.Saml;

using Shouldly;

namespace Assimalign.Cohesion.IdentityModel.Token.Saml.Tests;

/// <summary>
/// Contains unit tests for the SAML token model.
/// </summary>
public sealed class SamlTokenTests
{
    [Fact]
    [DisplayName("Cohesion Test [IdentityModel.Token.Saml] - SamlToken: Should expose assertion metadata")]
    public void SamlToken_WhenConstructed_ShouldExposeAssertionMetadata()
    {
        // Arrange
        var descriptor = new SamlTokenDescriptor
        {
            AssertionId = "_assertion-001",
            NameIdentifier = "user-42",
            ConfirmationMethod = "urn:oasis:names:tc:SAML:2.0:cm:bearer",
            Issuer = "https://issuer.cohesion.local",
            Version = "2.0",
            AssertionXml = "<Assertion ID=\"_assertion-001\" />"
        };

        descriptor.Claims.Add(new IdentityTokenClaim("role", "reader"));
        descriptor.Conditions.Add("AudienceRestriction", "api://orders");

        // Act
        var token = new SamlToken(descriptor);

        // Assert
        token.Kind.ShouldBe(IdentityTokenKind.Saml);
        token.AssertionId.ShouldBe("_assertion-001");
        token.NameIdentifier.ShouldBe("user-42");
        token.Subject.ShouldBe("user-42");
        token.ConfirmationMethod.ShouldBe("urn:oasis:names:tc:SAML:2.0:cm:bearer");
        token.Version.ShouldBe("2.0");
        token.AssertionXml.ShouldBe("<Assertion ID=\"_assertion-001\" />");
        token.TryGetCondition("AudienceRestriction", out var audience).ShouldBeTrue();
        audience.ShouldBe("api://orders");
    }
}
