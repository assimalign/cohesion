using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.Saml;
using Assimalign.Cohesion.IdentityModel.Protocols.Saml;

namespace Assimalign.Cohesion.IdentityModel.Tests;

/// <summary>
/// Guards the deliberate SAML mirrors across independent family branches. The SAML token package
/// (token branch) cannot reference the SAML protocol package (protocol branch), so it re-mints the
/// bearer confirmation-method constant and the NameID→<see cref="SubjectIdentifier" /> recipe.
/// This test — the only place that can reference both branches — pins them equivalent so
/// single-logout correlation and cross-branch subject equality cannot silently drift.
/// </summary>
public sealed class IdentityModelSamlMirrorTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML Mirror: The bearer confirmation method should match")]
    public void BearerConfirmationMethod_ShouldMatch()
    {
        Token.Saml.SamlConfirmationMethods.Bearer
            .ShouldBe(Protocols.Saml.SamlConfirmationMethods.Bearer);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityModel] - SAML Mirror: The NameID recipe should produce an equal subject identifier")]
    [InlineData("user@example.com", SubjectIdentifierFormats.EmailAddress, null, null, null)]
    [InlineData("user@example.com", SubjectIdentifierFormats.EmailAddress, "https://nq", "https://spq", null)]
    [InlineData("persistent-abc", SubjectIdentifierFormats.Persistent, "https://nq", "https://spq", "legacy-42")]
    public void NameIdRecipe_ShouldProduceEqualSubjectIdentifier(
        string value,
        string format,
        string? nameQualifier,
        string? spNameQualifier,
        string? spProvidedId)
    {
        const string issuerFallback = "https://idp.example.com";

        var tokenNameId = new Token.Saml.SamlNameId(value, format, nameQualifier, spNameQualifier, spProvidedId);
        var protocolNameId = new Protocols.Saml.SamlNameId(value, format, nameQualifier, spNameQualifier, spProvidedId);

        var tokenSubject = tokenNameId.GetSubjectIdentifier(issuerFallback);
        var protocolSubject = protocolNameId.GetSubjectIdentifier(issuerFallback);

        // Equality spans (Value, Format, Issuer, RelyingPartyQualifier).
        tokenSubject.ShouldBe(protocolSubject);

        // SPProvidedID rides Properties (excluded from equality) under the exact same key.
        if (spProvidedId is not null)
        {
            tokenSubject.Properties["SPProvidedID"].ShouldBe(spProvidedId);
            protocolSubject.Properties["SPProvidedID"].ShouldBe(spProvidedId);
        }
    }
}
