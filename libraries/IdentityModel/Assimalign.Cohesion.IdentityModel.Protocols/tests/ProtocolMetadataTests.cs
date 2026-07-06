using System;
using System.Linq;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Protocols.Tests.TestObjects;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Tests;

/// <summary>
/// Contains unit tests for the shared entity metadata base.
/// </summary>
public sealed class ProtocolMetadataTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Metadata should require an entity identifier")]
    public void Constructor_WhenEntityIdIsMissing_ShouldThrow()
    {
        Should.Throw<IdentityModelException>(() => new TestMetadata(new TestMetadataDescriptor(), AuthenticationProtocol.Saml2));
        Should.Throw<ArgumentNullException>(() => new TestMetadata(null!, AuthenticationProtocol.Saml2));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Metadata protocol should be pinned by the derived type")]
    public void Protocol_WhenMaterialized_ShouldBePinnedByTheDerivedType()
    {
        // Protocol is a constructor argument supplied by the derivative, never descriptor
        // data — a metadata object can never claim a protocol contradicting its type.
        var descriptor = new TestMetadataDescriptor { EntityId = "https://idp.example" };

        var metadata = new TestMetadata(descriptor, AuthenticationProtocol.Saml2);

        metadata.Protocol.ShouldBe(AuthenticationProtocol.Saml2);
        metadata.EntityId.ShouldBe("https://idp.example");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Metadata should preserve role scoping for dual-role entities")]
    public void RoleScoping_WhenEntityPlaysTwoRoles_ShouldBeQueryable()
    {
        // A federation proxy is both IdP and SP with disjoint keys. Selecting keys for
        // "verify IdP-issued assertions" must not surface the SP role's keys.
        var descriptor = new TestMetadataDescriptor { EntityId = "https://proxy.example" };
        descriptor.Roles.Add(ProtocolRole.IdentityProvider);
        descriptor.Roles.Add(ProtocolRole.RelyingParty);
        descriptor.Keys.Add(new ProtocolKey(new ProtocolKeyDescriptor
        {
            KeyId = "idp-signing",
            Use = ProtocolKeyUse.Signing,
            Role = ProtocolRole.IdentityProvider,
        }));
        descriptor.Keys.Add(new ProtocolKey(new ProtocolKeyDescriptor
        {
            KeyId = "sp-signing",
            Use = ProtocolKeyUse.Signing,
            Role = ProtocolRole.RelyingParty,
        }));
        descriptor.Endpoints.Add(new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Kind = new ProtocolEndpointKind("single-sign-on"),
            Location = "https://proxy.example/sso",
            Binding = ProtocolBinding.HttpRedirect,
            Role = ProtocolRole.IdentityProvider,
        }));

        var metadata = new TestMetadata(descriptor, AuthenticationProtocol.Saml2);

        var idpSigningKeys = metadata.Keys
            .Where(key => key.CanSign && key.Role == ProtocolRole.IdentityProvider)
            .ToArray();

        idpSigningKeys.Length.ShouldBe(1);
        idpSigningKeys[0].KeyId.ShouldBe("idp-signing");
        metadata.Roles.Count.ShouldBe(2);
        metadata.Endpoints[0].Role.ShouldBe(ProtocolRole.IdentityProvider);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Metadata should snapshot and preserve provenance")]
    public void Constructor_WhenConstructed_ShouldSnapshotAndPreserveProvenance()
    {
        // Arrange
        var validUntil = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var descriptor = new TestMetadataDescriptor
        {
            EntityId = "https://op.example",
            ValidUntil = validUntil,
            CacheDuration = TimeSpan.FromHours(24),
            RawDocument = "{\"issuer\":\"https://op.example\"}",
        };
        descriptor.Roles.Add(ProtocolRole.IdentityProvider);

        // Act
        var metadata = new TestMetadata(descriptor, AuthenticationProtocol.OpenIdConnect);
        descriptor.Roles.Add(ProtocolRole.RelyingParty);

        // Assert — the raw document survives for later signature re-verification.
        metadata.Roles.Count.ShouldBe(1);
        metadata.ValidUntil.ShouldBe(validUntil);
        metadata.CacheDuration.ShouldBe(TimeSpan.FromHours(24));
        metadata.RawDocument.ShouldBe("{\"issuer\":\"https://op.example\"}");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - Protocols: Metadata lists should reject null entries")]
    public void Constructor_WhenListsContainNull_ShouldThrow()
    {
        var descriptor = new TestMetadataDescriptor { EntityId = "https://idp.example" };
        descriptor.Keys.Add(null!);

        Should.Throw<IdentityModelException>(() => new TestMetadata(descriptor, AuthenticationProtocol.Saml2));
    }
}
