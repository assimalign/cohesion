using System;
using System.Linq;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect.Tests;

/// <summary>
/// Contains unit tests for the registered client metadata and the registration request
/// contracts.
/// </summary>
public sealed class OpenIdConnectClientMetadataTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Client metadata should project logout endpoints for neutral orchestration")]
    public void Constructor_WhenConstructed_ShouldProjectLogoutEndpoints()
    {
        // Arrange
        var descriptor = new OpenIdConnectClientMetadataDescriptor
        {
            ClientId = "s6BhdRkqt3",
            ClientName = "Example RP",
            BackChannelLogoutUri = "https://rp.example/backchannel-logout",
            FrontChannelLogoutUri = "https://rp.example/frontchannel-logout",
            JwksUri = "https://rp.example/jwks.json",
            ClientSecretExpiresAt = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        descriptor.RedirectUris.Add("https://rp.example/cb");

        // Act
        var metadata = new OpenIdConnectClientMetadata(descriptor);

        // Assert — protocol-neutral SLO fan-out enumerates the base endpoint list.
        metadata.ClientId.ShouldBe("s6BhdRkqt3");
        metadata.Roles.ShouldContain(ProtocolRole.RelyingParty);
        metadata.Endpoints.Single(e => e.Kind == OpenIdConnectEndpointKinds.BackChannelLogout)
            .Binding.ShouldBe(ProtocolBinding.BackChannel);
        metadata.Endpoints.Single(e => e.Kind == OpenIdConnectEndpointKinds.FrontChannelLogout)
            .Binding.ShouldBe(ProtocolBinding.HttpRedirect);
        metadata.Endpoints.Single(e => e.Kind == OpenIdConnectEndpointKinds.Jwks)
            .Location.ShouldBe("https://rp.example/jwks.json");

        // Redirect URIs are per-request destinations, deliberately not endpoints.
        metadata.RedirectUris.ShouldBe(["https://rp.example/cb"]);
        metadata.Endpoints.Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Client metadata validation should implement the registration rules")]
    public void Validate_WhenRecordIsNonConformant_ShouldReportRegistrationRules()
    {
        // A redirect-grant client without redirect URIs is an error.
        var missingRedirects = new OpenIdConnectClientMetadataDescriptor { ClientId = "client-1" };
        new OpenIdConnectClientMetadata(missingRedirects).Validate()
            .Errors.ShouldContain(d => d.Member == "redirect_uris");

        // A pairwise client with redirect hosts on multiple domains needs a sector.
        var pairwise = new OpenIdConnectClientMetadataDescriptor
        {
            ClientId = "client-2",
            SubjectType = SubjectIdentifierFormats.Pairwise,
        };
        pairwise.RedirectUris.Add("https://app-one.example/cb");
        pairwise.RedirectUris.Add("https://app-two.example/cb");

        new OpenIdConnectClientMetadata(pairwise).Validate()
            .Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.SectorIdentifierInvalid);

        // Declaring both jwks and jwks_uri is prohibited by RFC 7591.
        var jwksConflict = new OpenIdConnectClientMetadataDescriptor
        {
            ClientId = "client-3",
            JwksUri = "https://rp.example/jwks.json",
        };
        jwksConflict.RedirectUris.Add("https://rp.example/cb");
        jwksConflict.Properties["jwks"] = "{\"keys\":[]}";

        new OpenIdConnectClientMetadata(jwksConflict).Validate()
            .Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.JwksConflict);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Registration requests have no client identifier by definition")]
    public void RegistrationRequest_WhenConstructed_ShouldRejectAClientId()
    {
        // Arrange — the same descriptor shape describes the pre-registration request.
        var descriptor = new OpenIdConnectClientMetadataDescriptor
        {
            ApplicationType = "web",
            ClientName = "New RP",
        };
        descriptor.RedirectUris.Add("https://new-rp.example/cb");
        descriptor.GrantTypes.Add(OpenIdConnectGrantTypes.AuthorizationCode);

        // Act
        var request = new OpenIdConnectClientRegistrationRequest(descriptor);

        // Assert — the request carries the metadata; the identifier arrives in the
        // registration response.
        request.RedirectUris.ShouldBe(["https://new-rp.example/cb"]);
        request.ClientName.ShouldBe("New RP");

        descriptor.ClientId = "assigned-too-early";
        Should.Throw<IdentityModelException>(() => new OpenIdConnectClientRegistrationRequest(descriptor));
    }
}
