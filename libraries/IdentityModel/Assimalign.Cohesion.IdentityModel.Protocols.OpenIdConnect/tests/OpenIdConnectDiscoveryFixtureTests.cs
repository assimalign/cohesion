using System.Text.Json;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;
using Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect.Tests;

/// <summary>
/// Specification-oriented compliance fixtures for discovery metadata: wire-shaped JSON
/// documents (modeled on the OpenID Connect Discovery 1.0 §4.2 example) are materialized
/// through a reflection-free <see cref="JsonDocument" /> reader — the same AOT-safe path
/// implementation packages use — and asserted against the contract and its validation.
/// </summary>
public sealed class OpenIdConnectDiscoveryFixtureTests
{
    private const string ConformantDiscoveryDocument = """
        {
          "issuer": "https://server.example.com",
          "authorization_endpoint": "https://server.example.com/connect/authorize",
          "token_endpoint": "https://server.example.com/connect/token",
          "userinfo_endpoint": "https://server.example.com/connect/userinfo",
          "jwks_uri": "https://server.example.com/jwks.json",
          "registration_endpoint": "https://server.example.com/connect/register",
          "end_session_endpoint": "https://server.example.com/connect/end_session",
          "scopes_supported": ["openid", "profile", "email"],
          "response_types_supported": ["code", "code id_token", "id_token"],
          "subject_types_supported": ["public", "pairwise"],
          "id_token_signing_alg_values_supported": ["RS256", "ES256"],
          "claims_supported": ["sub", "iss", "auth_time", "acr", "name", "email"],
          "code_challenge_methods_supported": ["S256"],
          "backchannel_logout_supported": true,
          "backchannel_logout_session_supported": true,
          "authorization_response_iss_parameter_supported": true
        }
        """;

    private static OpenIdConnectProviderMetadata Materialize(string json)
    {
        // Reflection-free materialization: JsonDocument -> descriptor -> contract.
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var descriptor = new OpenIdConnectProviderMetadataDescriptor
        {
            Issuer = GetString(root, "issuer"),
            AuthorizationEndpoint = GetString(root, "authorization_endpoint"),
            TokenEndpoint = GetString(root, "token_endpoint"),
            UserInfoEndpoint = GetString(root, "userinfo_endpoint"),
            JwksUri = GetString(root, "jwks_uri"),
            RegistrationEndpoint = GetString(root, "registration_endpoint"),
            EndSessionEndpoint = GetString(root, "end_session_endpoint"),
            BackChannelLogoutSupported = GetBoolean(root, "backchannel_logout_supported"),
            BackChannelLogoutSessionSupported = GetBoolean(root, "backchannel_logout_session_supported"),
            AuthorizationResponseIssParameterSupported = GetBoolean(root, "authorization_response_iss_parameter_supported"),
            RawDocument = json,
        };

        CopyStrings(root, "scopes_supported", descriptor.ScopesSupported);
        CopyStrings(root, "response_types_supported", descriptor.ResponseTypesSupported);
        CopyStrings(root, "subject_types_supported", descriptor.SubjectTypesSupported);
        CopyStrings(root, "id_token_signing_alg_values_supported", descriptor.IdTokenSigningAlgValuesSupported);
        CopyStrings(root, "claims_supported", descriptor.ClaimsSupported);
        CopyStrings(root, "code_challenge_methods_supported", descriptor.CodeChallengeMethodsSupported);

        return new OpenIdConnectProviderMetadata(descriptor);

        static string? GetString(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) ? value.GetString() : null;

        static bool? GetBoolean(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) ? value.GetBoolean() : null;

        static void CopyStrings(JsonElement element, string name, System.Collections.Generic.IList<string> target)
        {
            if (!element.TryGetProperty(name, out var array))
            {
                return;
            }

            foreach (var entry in array.EnumerateArray())
            {
                target.Add(entry.GetString()!);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: The Discovery example document should materialize and validate clean")]
    public void Materialize_WhenDocumentIsConformant_ShouldValidateClean()
    {
        // Act
        var metadata = Materialize(ConformantDiscoveryDocument);
        var result = metadata.Validate(expectedIssuer: "https://server.example.com");

        // Assert
        result.Succeeded.ShouldBeTrue();
        metadata.Issuer.ShouldBe("https://server.example.com");
        metadata.ResponseTypesSupported.Count.ShouldBe(3);
        metadata.AuthorizationResponseIssParameterSupported.ShouldBe(true);
        metadata.RawDocument.ShouldBe(ConformantDiscoveryDocument);
        metadata.Endpoints.Count.ShouldBe(6);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel] - OIDC: A non-conformant discovery document should stay diagnosable")]
    public void Materialize_WhenDocumentOmitsRequiredMembers_ShouldDiagnoseNotThrow()
    {
        // Arrange — a real-world non-conformance: no jwks_uri, no subject types.
        const string nonConformant = """
            {
              "issuer": "https://broken.example.com",
              "authorization_endpoint": "https://broken.example.com/authorize",
              "token_endpoint": "https://broken.example.com/token",
              "response_types_supported": ["code"],
              "id_token_signing_alg_values_supported": ["RS256"]
            }
            """;

        // Act — materialization must hold the document; validation diagnoses it.
        var metadata = Materialize(nonConformant);
        var result = metadata.Validate();

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(d => d.Member == "jwks_uri");
        result.Errors.ShouldContain(d => d.Member == "subject_types_supported");
        result.Diagnostics.ShouldContain(d =>
            d.Severity == ProtocolValidationSeverity.Warning && d.Member == "userinfo_endpoint");
    }
}
