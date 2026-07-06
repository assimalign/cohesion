using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml.Tests;

/// <summary>
/// Verifies the SAML single-logout contracts (SAML Core §3.7): the <c>LogoutRequest</c>
/// projection onto the shared logout envelope and the partial-logout response status.
/// </summary>
public sealed class SamlLogoutTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: Apply should derive the base subject through the pinned recipe")]
    public void Apply_WhenCalled_ShouldDeriveSubjectAndSessionIds()
    {
        var descriptor = new SamlLogoutRequestDescriptor
        {
            MessageId = "_logout-1",
            Issuer = SamlAssertionFixtures.Issuer,
        };
        var nameId = new SamlNameId(
            "persistent-abc",
            format: SamlNameIdFormats.Persistent,
            spNameQualifier: SamlAssertionFixtures.RelyingParty);

        descriptor.Apply(nameId, new[] { "sess-1", "sess-2" });

        // The base subject must equal what the login leg would have produced from the same
        // NameID, defaulting the name qualifier to the message issuer.
        descriptor.Subject.ShouldBe(nameId.GetSubjectIdentifier(SamlAssertionFixtures.Issuer));
        descriptor.ProviderSessionIds.ShouldBe(new[] { "sess-1", "sess-2" });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A materialized logout request should keep the NameID and session ids")]
    public void LogoutRequest_WhenMaterialized_ShouldExposeNameIdAndSessionScope()
    {
        var descriptor = new SamlLogoutRequestDescriptor
        {
            MessageId = "_logout-1",
            Issuer = SamlAssertionFixtures.Issuer,
        };
        var nameId = new SamlNameId("persistent-abc", format: SamlNameIdFormats.Persistent);
        descriptor.Apply(nameId, new[] { "sess-1" });

        var request = new SamlLogoutRequest(descriptor);

        request.NameId.ShouldBe(nameId);
        request.Issuer.ShouldBe(SamlAssertionFixtures.Issuer);
        request.ProviderSessionIds.ShouldBe(new[] { "sess-1" });
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A logout request requires a message identifier")]
    public void LogoutRequest_WhenNoMessageId_ShouldThrow()
    {
        Should.Throw<IdentityModelException>(() => new SamlLogoutRequest(new SamlLogoutRequestDescriptor
        {
            Issuer = SamlAssertionFixtures.Issuer,
        }));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A partial logout response should succeed with a sub-code")]
    public void LogoutResponse_WhenPartial_ShouldSucceedWithPartialLogoutSubCode()
    {
        var response = new SamlLogoutResponse(new SamlLogoutResponseDescriptor
        {
            MessageId = "_logout-resp-1",
            Issuer = SamlAssertionFixtures.Issuer,
            InResponseTo = "_logout-1",
            Status = ProtocolResponseStatus.SuccessWith(
                SamlStatusCodes.Success,
                subCodes: new[] { SamlStatusCodes.PartialLogout }),
        });

        response.Status.Succeeded.ShouldBeTrue();
        response.Status.SubCodes.ShouldContain(SamlStatusCodes.PartialLogout);
    }
}
