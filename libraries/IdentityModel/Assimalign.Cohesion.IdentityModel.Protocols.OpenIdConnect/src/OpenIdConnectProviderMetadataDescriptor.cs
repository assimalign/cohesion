using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Describes the contents of an OpenID Provider discovery document before it is
/// materialized into an immutable <see cref="OpenIdConnectProviderMetadata" />.
/// </summary>
/// <remarks>
/// The typed members are the primary input: materialization projects the well-formed typed
/// endpoint values into the inherited base endpoint list with their kinds and bindings.
/// The inherited <see cref="ProtocolMetadataDescriptor.Endpoints" /> list is reserved for
/// extension endpoints (revocation, introspection, pushed authorization, and similar);
/// entries whose kind collides with a typed member are rejected at materialization.
/// </remarks>
public class OpenIdConnectProviderMetadataDescriptor : ProtocolMetadataDescriptor
{
    /// <summary>
    /// Gets or sets the issuer identifier. Alias of
    /// <see cref="ProtocolMetadataDescriptor.EntityId" />.
    /// </summary>
    public string? Issuer
    {
        get => EntityId;
        set => EntityId = value;
    }

    /// <summary>
    /// Gets or sets the authorization endpoint URL (<c>authorization_endpoint</c>).
    /// </summary>
    public string? AuthorizationEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the token endpoint URL (<c>token_endpoint</c>).
    /// </summary>
    public string? TokenEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the UserInfo endpoint URL (<c>userinfo_endpoint</c>).
    /// </summary>
    public string? UserInfoEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the JWK Set document URL (<c>jwks_uri</c>).
    /// </summary>
    public string? JwksUri { get; set; }

    /// <summary>
    /// Gets or sets the dynamic client registration endpoint URL
    /// (<c>registration_endpoint</c>).
    /// </summary>
    public string? RegistrationEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the RP-initiated logout endpoint URL (<c>end_session_endpoint</c>).
    /// </summary>
    public string? EndSessionEndpoint { get; set; }

    /// <summary>
    /// Gets the supported scope values (<c>scopes_supported</c>).
    /// </summary>
    public IList<string> ScopesSupported { get; } = new List<string>();

    /// <summary>
    /// Gets the supported response type values (<c>response_types_supported</c>).
    /// </summary>
    public IList<string> ResponseTypesSupported { get; } = new List<string>();

    /// <summary>
    /// Gets the supported response mode values (<c>response_modes_supported</c>).
    /// </summary>
    public IList<string> ResponseModesSupported { get; } = new List<string>();

    /// <summary>
    /// Gets the supported grant type values (<c>grant_types_supported</c>).
    /// </summary>
    public IList<string> GrantTypesSupported { get; } = new List<string>();

    /// <summary>
    /// Gets the supported subject identifier types (<c>subject_types_supported</c>).
    /// </summary>
    public IList<string> SubjectTypesSupported { get; } = new List<string>();

    /// <summary>
    /// Gets the supported ID token signing algorithms
    /// (<c>id_token_signing_alg_values_supported</c>).
    /// </summary>
    public IList<string> IdTokenSigningAlgValuesSupported { get; } = new List<string>();

    /// <summary>
    /// Gets the supported claim names (<c>claims_supported</c>).
    /// </summary>
    public IList<string> ClaimsSupported { get; } = new List<string>();

    /// <summary>
    /// Gets the supported PKCE code challenge methods
    /// (<c>code_challenge_methods_supported</c>).
    /// </summary>
    public IList<string> CodeChallengeMethodsSupported { get; } = new List<string>();

    /// <summary>
    /// Gets the supported token endpoint authentication methods
    /// (<c>token_endpoint_auth_methods_supported</c>).
    /// </summary>
    public IList<string> TokenEndpointAuthMethodsSupported { get; } = new List<string>();

    /// <summary>
    /// Gets the supported authentication context class references
    /// (<c>acr_values_supported</c>).
    /// </summary>
    public IList<string> AcrValuesSupported { get; } = new List<string>();

    /// <summary>
    /// Gets or sets whether front-channel logout is supported
    /// (<c>frontchannel_logout_supported</c>). Null preserves an absent wire member.
    /// </summary>
    public bool? FrontChannelLogoutSupported { get; set; }

    /// <summary>
    /// Gets or sets whether front-channel logout receives a session identifier
    /// (<c>frontchannel_logout_session_supported</c>).
    /// </summary>
    public bool? FrontChannelLogoutSessionSupported { get; set; }

    /// <summary>
    /// Gets or sets whether back-channel logout is supported
    /// (<c>backchannel_logout_supported</c>).
    /// </summary>
    public bool? BackChannelLogoutSupported { get; set; }

    /// <summary>
    /// Gets or sets whether back-channel logout tokens carry a session identifier
    /// (<c>backchannel_logout_session_supported</c>).
    /// </summary>
    public bool? BackChannelLogoutSessionSupported { get; set; }

    /// <summary>
    /// Gets or sets whether the <c>claims</c> request parameter is supported
    /// (<c>claims_parameter_supported</c>).
    /// </summary>
    public bool? ClaimsParameterSupported { get; set; }

    /// <summary>
    /// Gets or sets whether the <c>request</c> parameter is supported
    /// (<c>request_parameter_supported</c>).
    /// </summary>
    public bool? RequestParameterSupported { get; set; }

    /// <summary>
    /// Gets or sets whether the <c>request_uri</c> parameter is supported
    /// (<c>request_uri_parameter_supported</c>).
    /// </summary>
    public bool? RequestUriParameterSupported { get; set; }

    /// <summary>
    /// Gets or sets whether authorization responses carry the RFC 9207 <c>iss</c>
    /// parameter (<c>authorization_response_iss_parameter_supported</c>).
    /// </summary>
    public bool? AuthorizationResponseIssParameterSupported { get; set; }
}
