using System;
using System.Linq;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel.Protocols;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml.Tests;

/// <summary>
/// Verifies the SAML <c>EntityDescriptor</c> contract: the role-scoped projection of role
/// descriptor endpoints and keys onto the inherited flat lists (every projected entry stamped
/// with its enclosing role), role lookup, and the metadata conformance rules.
/// </summary>
public sealed class SamlEntityMetadataTests
{
    private const string EntityId = "https://idp.example.com/saml";

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A dual-role entity should stamp every projected endpoint with its role")]
    public void Materialize_WhenDualRole_ShouldStampProjectedEndpointsWithRole()
    {
        var metadata = new SamlEntityMetadata(DualRoleDescriptor());

        // Every base endpoint must carry a non-null role, and it must match the role
        // descriptor it came from — the SSO endpoint is the identity provider's, the ACS
        // endpoint the relying party's.
        metadata.Endpoints.ShouldAllBe(endpoint => endpoint.Role != null);

        var sso = metadata.Endpoints.Single(endpoint => endpoint.Location == "https://idp.example.com/sso");
        sso.Role.ShouldBe(ProtocolRole.IdentityProvider);

        var acs = metadata.Endpoints.Single(endpoint => endpoint.Location == SamlAssertionFixtures.AcsUrl);
        acs.Role.ShouldBe(ProtocolRole.RelyingParty);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A dual-role entity should stamp every projected key with its role")]
    public void Materialize_WhenDualRole_ShouldStampProjectedKeysWithRole()
    {
        var metadata = new SamlEntityMetadata(DualRoleDescriptor());

        metadata.Keys.ShouldAllBe(key => key.Role != null);
        metadata.Keys.Single(key => key.KeyId == "idp-signing").Role.ShouldBe(ProtocolRole.IdentityProvider);
        metadata.Keys.Single(key => key.KeyId == "sp-signing").Role.ShouldBe(ProtocolRole.RelyingParty);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: The entity should expose both roles")]
    public void Materialize_WhenDualRole_ShouldExposeBothRoles()
    {
        var metadata = new SamlEntityMetadata(DualRoleDescriptor());

        metadata.Roles.ShouldContain(ProtocolRole.IdentityProvider);
        metadata.Roles.ShouldContain(ProtocolRole.RelyingParty);
        metadata.GetRoleDescriptor(ProtocolRole.IdentityProvider).ShouldNotBeNull();
        metadata.GetRoleDescriptor(ProtocolRole.RelyingParty).ShouldNotBeNull();
        metadata.GetRoleDescriptor(ProtocolRole.AuthorizationServer).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A conformant entity descriptor should validate")]
    public void Validate_WhenConformant_ShouldSucceed()
    {
        var metadata = new SamlEntityMetadata(DualRoleDescriptor());

        metadata.Validate(SamlAssertionFixtures.Now).Succeeded.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: An entity with no role descriptor should fail")]
    public void Validate_WhenNoRoleDescriptor_ShouldReportRoleDescriptorMissing()
    {
        var descriptor = new SamlEntityMetadataDescriptor { EntityId = EntityId };
        var metadata = new SamlEntityMetadata(descriptor);

        var result = metadata.Validate();

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == SamlValidationCodes.RoleDescriptorMissing);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: An expired entity descriptor should fail")]
    public void Validate_WhenExpired_ShouldReportExpired()
    {
        var descriptor = DualRoleDescriptor();
        descriptor.ValidUntil = SamlAssertionFixtures.Now.AddDays(-1);
        var metadata = new SamlEntityMetadata(descriptor);

        var result = metadata.Validate(SamlAssertionFixtures.Now);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == ProtocolValidationCodes.Expired);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - SAML: A role descriptor without protocol support should warn")]
    public void Validate_WhenNoProtocolSupport_ShouldWarnButSucceed()
    {
        var descriptor = new SamlEntityMetadataDescriptor { EntityId = EntityId };
        descriptor.RoleDescriptors.Add(new SamlRoleDescriptor(
            ProtocolRole.IdentityProvider,
            endpoints: new[] { Endpoint("https://idp.example.com/sso", ProtocolBinding.HttpRedirect) }));
        var metadata = new SamlEntityMetadata(descriptor);

        var result = metadata.Validate();

        result.Succeeded.ShouldBeTrue();
        result.Diagnostics.ShouldContain(diagnostic =>
            diagnostic.Code == ProtocolValidationCodes.MissingRecommendedMember &&
            diagnostic.Severity == ProtocolValidationSeverity.Warning);
    }

    private static SamlEntityMetadataDescriptor DualRoleDescriptor()
    {
        var descriptor = new SamlEntityMetadataDescriptor { EntityId = EntityId };

        // Author the role descriptors' endpoints/keys with NO role scope, to prove
        // materialization stamps the enclosing role rather than trusting the caller.
        descriptor.RoleDescriptors.Add(new SamlRoleDescriptor(
            ProtocolRole.IdentityProvider,
            endpoints: new[] { Endpoint("https://idp.example.com/sso", ProtocolBinding.HttpRedirect) },
            keys: new[] { SigningKey("idp-signing") },
            protocolSupportEnumeration: SamlConstants.ProtocolNamespace));

        descriptor.RoleDescriptors.Add(new SamlRoleDescriptor(
            ProtocolRole.RelyingParty,
            endpoints: new[] { Endpoint(SamlAssertionFixtures.AcsUrl, ProtocolBinding.HttpPost) },
            keys: new[] { SigningKey("sp-signing") },
            protocolSupportEnumeration: SamlConstants.ProtocolNamespace));

        return descriptor;
    }

    private static ProtocolEndpoint Endpoint(string location, ProtocolBinding binding)
        => new(new ProtocolEndpointDescriptor { Location = location, Binding = binding });

    private static ProtocolKey SigningKey(string keyId)
    {
        var descriptor = new ProtocolKeyDescriptor { KeyId = keyId, Use = ProtocolKeyUse.Signing };
        descriptor.Certificates.Add("MIIB...base64der");
        return new ProtocolKey(descriptor);
    }
}
