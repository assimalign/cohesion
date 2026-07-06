using System;
using System.Linq;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect.Tests;

/// <summary>
/// Contains unit tests for the OpenID Provider metadata contract.
/// </summary>
public sealed class OpenIdConnectProviderMetadataTests
{
    private static OpenIdConnectProviderMetadataDescriptor CreateConformantDescriptor()
    {
        var descriptor = new OpenIdConnectProviderMetadataDescriptor
        {
            Issuer = "https://op.example",
            AuthorizationEndpoint = "https://op.example/authorize",
            TokenEndpoint = "https://op.example/token",
            UserInfoEndpoint = "https://op.example/userinfo",
            JwksUri = "https://op.example/jwks",
        };
        descriptor.ResponseTypesSupported.Add("code");
        descriptor.SubjectTypesSupported.Add("public");
        descriptor.IdTokenSigningAlgValuesSupported.Add("RS256");
        descriptor.ScopesSupported.Add("openid");
        descriptor.ClaimsSupported.Add("sub");
        return descriptor;
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Provider metadata should project typed endpoints into the base list")]
    public void Constructor_WhenConstructed_ShouldProjectTypedEndpointsIntoTheBaseList()
    {
        // Arrange + Act
        var metadata = new OpenIdConnectProviderMetadata(CreateConformantDescriptor());

        // Assert — the typed members store wire truth and the base list is the projection,
        // so protocol-neutral consumers enumerate endpoints without OIDC knowledge.
        metadata.Issuer.ShouldBe("https://op.example");
        metadata.Protocol.ShouldBe(AuthenticationProtocol.OpenIdConnect);
        metadata.Endpoints.Count.ShouldBe(4);

        var authorization = metadata.Endpoints.Single(e => e.Kind == OpenIdConnectEndpointKinds.Authorization);
        authorization.Location.ShouldBe("https://op.example/authorize");
        authorization.Binding.ShouldBe(ProtocolBinding.HttpRedirect);

        var token = metadata.Endpoints.Single(e => e.Kind == OpenIdConnectEndpointKinds.Token);
        token.Binding.ShouldBe(ProtocolBinding.BackChannel);

        metadata.Roles.ShouldContain(ProtocolRole.IdentityProvider);
        metadata.Roles.ShouldContain(ProtocolRole.AuthorizationServer);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Malformed typed endpoints should stay diagnosable")]
    public void Validate_WhenTypedEndpointIsMalformed_ShouldReportInvalidEndpoint()
    {
        // Arrange — a malformed wire value must materialize (wire fidelity) but be
        // omitted from the base projection and reported by validation.
        var descriptor = CreateConformantDescriptor();
        descriptor.UserInfoEndpoint = "//scheme-relative.example/userinfo";

        // Act
        var metadata = new OpenIdConnectProviderMetadata(descriptor);
        var result = metadata.Validate();

        // Assert
        metadata.UserInfoEndpoint.ShouldBe("//scheme-relative.example/userinfo");
        metadata.Endpoints.ShouldNotContain(e => e.Kind == OpenIdConnectEndpointKinds.UserInfo);
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.Code == ProtocolValidationCodes.InvalidEndpoint && d.Member == "userinfo_endpoint");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Extension endpoints are allowed, colliding kinds are not")]
    public void Constructor_WhenExtensionEndpointsProvided_ShouldMergeOrRejectByKind()
    {
        // Arrange — revocation (RFC 7009) has no typed member and rides the base list.
        var descriptor = CreateConformantDescriptor();
        descriptor.Endpoints.Add(new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Kind = new ProtocolEndpointKind("revocation"),
            Location = "https://op.example/revoke",
            Binding = ProtocolBinding.BackChannel,
        }));

        var metadata = new OpenIdConnectProviderMetadata(descriptor);
        metadata.Endpoints.ShouldContain(e => e.Kind == new ProtocolEndpointKind("revocation"));

        // A colliding kind would create two input paths for one member.
        var colliding = CreateConformantDescriptor();
        colliding.Endpoints.Add(new ProtocolEndpoint(new ProtocolEndpointDescriptor
        {
            Kind = OpenIdConnectEndpointKinds.Token,
            Location = "https://op.example/other-token",
            Binding = ProtocolBinding.BackChannel,
        }));

        Should.Throw<IdentityModelException>(() => new OpenIdConnectProviderMetadata(colliding));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: Provider metadata validation should implement the Discovery required set")]
    public void Validate_WhenDocumentIsNonConformant_ShouldReportTheDiscoveryRules()
    {
        // Conformant document validates clean.
        new OpenIdConnectProviderMetadata(CreateConformantDescriptor()).Validate().Succeeded.ShouldBeTrue();

        // Missing jwks_uri and RS256 are Discovery §3 errors.
        var missing = CreateConformantDescriptor();
        missing.JwksUri = null;
        missing.IdTokenSigningAlgValuesSupported.Clear();
        missing.IdTokenSigningAlgValuesSupported.Add("ES256");

        var result = new OpenIdConnectProviderMetadata(missing).Validate();

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.Code == ProtocolValidationCodes.MissingRequiredMember && d.Member == "jwks_uri");
        result.Errors.ShouldContain(d => d.Code == OpenIdConnectValidationCodes.Rs256NotSupported);

        // A non-https issuer is an error; issuer mismatch against the retrieval issuer too.
        var httpIssuer = CreateConformantDescriptor();
        httpIssuer.Issuer = "http://op.example";
        new OpenIdConnectProviderMetadata(httpIssuer).Validate().Errors
            .ShouldContain(d => d.Code == ProtocolValidationCodes.ValueNotAllowed && d.Member == "issuer");

        new OpenIdConnectProviderMetadata(CreateConformantDescriptor()).Validate(expectedIssuer: "https://other.example")
            .Errors.ShouldContain(d => d.Code == ProtocolValidationCodes.IssuerMismatch);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: token_endpoint requiredness should be conditioned on non-implicit grants")]
    public void Validate_WhenImplicitOnly_ShouldNotRequireTheTokenEndpoint()
    {
        // Arrange — a legacy implicit-only OP legally has no token endpoint.
        var implicitOnly = CreateConformantDescriptor();
        implicitOnly.TokenEndpoint = null;
        implicitOnly.GrantTypesSupported.Add(OpenIdConnectGrantTypes.Implicit);

        var codeCapable = CreateConformantDescriptor();
        codeCapable.TokenEndpoint = null;

        // Assert
        new OpenIdConnectProviderMetadata(implicitOnly).Validate()
            .Errors.ShouldNotContain(d => d.Member == "token_endpoint");
        new OpenIdConnectProviderMetadata(codeCapable).Validate()
            .Errors.ShouldContain(d => d.Member == "token_endpoint");
    }
}
