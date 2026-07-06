using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml.Tests;

/// <summary>
/// Verifies the pinned NameID-to-<see cref="SubjectIdentifier" /> recipe. Because the login
/// leg (assertion subject) and the logout leg (logout NameID) both derive identifiers through
/// this one recipe, an identifier built from the same wire fields must be equal across legs,
/// or single-logout correlation silently fails.
/// </summary>
public sealed class SamlSubjectExtensionsTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: The recipe should map every NameID field")]
    public void GetSubjectIdentifier_WhenNameIdFullyPopulated_ShouldMapEveryField()
    {
        var nameId = new SamlNameId(
            "user@example.com",
            format: SamlNameIdFormats.EmailAddress,
            nameQualifier: "https://idp.example.com",
            spNameQualifier: "https://sp.example.com",
            spProvidedId: "legacy-42");

        var identifier = nameId.GetSubjectIdentifier();

        identifier.Value.ShouldBe("user@example.com");
        identifier.Format.ShouldBe(SamlNameIdFormats.EmailAddress);
        identifier.Issuer.ShouldBe("https://idp.example.com");
        identifier.RelyingPartyQualifier.ShouldBe("https://sp.example.com");
        identifier.Properties["SPProvidedID"].ShouldBe("legacy-42");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: An absent name qualifier should default to the issuer")]
    public void GetSubjectIdentifier_WhenNameQualifierAbsent_ShouldFallBackToIssuer()
    {
        var nameId = new SamlNameId("user@example.com", format: SamlNameIdFormats.EmailAddress);

        var identifier = nameId.GetSubjectIdentifier(issuerFallback: "https://idp.example.com");

        identifier.Issuer.ShouldBe("https://idp.example.com");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A present name qualifier should beat the fallback")]
    public void GetSubjectIdentifier_WhenNameQualifierPresent_ShouldKeepQualifier()
    {
        var nameId = new SamlNameId(
            "user@example.com",
            format: SamlNameIdFormats.EmailAddress,
            nameQualifier: "https://real-idp.example.com");

        var identifier = nameId.GetSubjectIdentifier(issuerFallback: "https://fallback.example.com");

        identifier.Issuer.ShouldBe("https://real-idp.example.com");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: The login and logout legs should produce equal identifiers")]
    public void GetSubjectIdentifier_WhenLoginAndLogoutLegsMatch_ShouldBeEqual()
    {
        // Same wire fields observed on the assertion (login) and the logout request must lift
        // to equal identifiers so the single-logout join keys on a stable value.
        var login = new SamlNameId(
            "persistent-abc",
            format: SamlNameIdFormats.Persistent,
            nameQualifier: "https://idp.example.com",
            spNameQualifier: "https://sp.example.com");
        var logout = new SamlNameId(
            "persistent-abc",
            format: SamlNameIdFormats.Persistent,
            nameQualifier: "https://idp.example.com",
            spNameQualifier: "https://sp.example.com");

        login.GetSubjectIdentifier().ShouldBe(logout.GetSubjectIdentifier());
    }
}
