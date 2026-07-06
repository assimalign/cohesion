namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Defines the OpenID-Connect-minted validation diagnostic codes. Cross-protocol concepts
/// (issuer mismatch, audience mismatch, missing required members, expiry) use the shared
/// <see cref="ProtocolValidationCodes" />; this class carries only diagnostics OpenID
/// Connect itself defines.
/// </summary>
public static class OpenIdConnectValidationCodes
{
    /// <summary>
    /// The <c>openid</c> scope is absent from an authentication request.
    /// </summary>
    public const string MissingOpenIdScope = "missing_openid_scope";

    /// <summary>
    /// A nonce is required for the requested flow but absent.
    /// </summary>
    public const string NonceMissing = "nonce_missing";

    /// <summary>
    /// The nonce does not match the expected value.
    /// </summary>
    public const string NonceMismatch = "nonce_mismatch";

    /// <summary>
    /// PKCE is expected but no code challenge is present, or the challenge method is not
    /// <c>S256</c>.
    /// </summary>
    public const string PkceMissing = "pkce_missing";

    /// <summary>
    /// <c>prompt=none</c> is combined with another prompt value.
    /// </summary>
    public const string PromptNoneCombined = "prompt_none_combined";

    /// <summary>
    /// The authorized party (<c>azp</c>) is absent or unexpected for a multi-audience
    /// token.
    /// </summary>
    public const string AzpInvalid = "azp_invalid";

    /// <summary>
    /// The authentication is older than the requested maximum age.
    /// </summary>
    public const string MaxAgeExceeded = "max_age_exceeded";

    /// <summary>
    /// The <c>auth_time</c> claim is required (a maximum age or an essential request was
    /// in effect) but absent.
    /// </summary>
    public const string AuthTimeMissing = "auth_time_missing";

    /// <summary>
    /// A logout token carries a prohibited <c>nonce</c> claim.
    /// </summary>
    public const string LogoutTokenNonceProhibited = "logout_token_nonce_prohibited";

    /// <summary>
    /// A logout token's <c>events</c> object is absent, lacks the back-channel logout
    /// event, or carries a non-object event value.
    /// </summary>
    public const string LogoutEventInvalid = "logout_event_invalid";

    /// <summary>
    /// A logout token identifies no subject: both <c>sub</c> and <c>sid</c> are absent.
    /// </summary>
    public const string LogoutSubjectMissing = "logout_subject_missing";

    /// <summary>
    /// A success token response is missing its <c>access_token</c>.
    /// </summary>
    public const string MissingAccessToken = "missing_access_token";

    /// <summary>
    /// A success token response is missing its <c>token_type</c>.
    /// </summary>
    public const string MissingTokenType = "missing_token_type";

    /// <summary>
    /// A success authorization response is missing an artifact the requested response
    /// type promises; one finding is produced per missing artifact.
    /// </summary>
    public const string MissingSuccessArtifact = "missing_success_artifact";

    /// <summary>
    /// The RFC 9207 <c>iss</c> response parameter is absent from a provider that
    /// advertises support for it.
    /// </summary>
    public const string IssParameterMissing = "iss_parameter_missing";

    /// <summary>
    /// The provider's <c>id_token_signing_alg_values_supported</c> does not include
    /// <c>RS256</c> (Discovery §3 requires it).
    /// </summary>
    public const string Rs256NotSupported = "rs256_not_supported";

    /// <summary>
    /// Client metadata carries both <c>jwks</c> and <c>jwks_uri</c> (RFC 7591 prohibits
    /// both).
    /// </summary>
    public const string JwksConflict = "jwks_conflict";

    /// <summary>
    /// A pairwise-subject client configuration is inconsistent (for example multiple
    /// redirect URI hosts without a sector identifier URI).
    /// </summary>
    public const string SectorIdentifierInvalid = "sector_identifier_invalid";
}
