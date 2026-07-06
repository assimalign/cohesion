namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Defines the wire error codes carried by OAuth 2.0 and OpenID Connect error responses
/// (the <c>error</c> parameter), as consumed through
/// <see cref="ProtocolResponseStatus.Code" />.
/// </summary>
public static class OpenIdConnectErrorCodes
{
    /// <summary>The <c>invalid_request</c> error (RFC 6749).</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>The <c>unauthorized_client</c> error (RFC 6749).</summary>
    public const string UnauthorizedClient = "unauthorized_client";

    /// <summary>The <c>access_denied</c> error (RFC 6749).</summary>
    public const string AccessDenied = "access_denied";

    /// <summary>The <c>unsupported_response_type</c> error (RFC 6749).</summary>
    public const string UnsupportedResponseType = "unsupported_response_type";

    /// <summary>The <c>invalid_scope</c> error (RFC 6749).</summary>
    public const string InvalidScope = "invalid_scope";

    /// <summary>The <c>server_error</c> error (RFC 6749).</summary>
    public const string ServerError = "server_error";

    /// <summary>The <c>temporarily_unavailable</c> error (RFC 6749).</summary>
    public const string TemporarilyUnavailable = "temporarily_unavailable";

    /// <summary>The <c>invalid_client</c> token endpoint error (RFC 6749 §5.2).</summary>
    public const string InvalidClient = "invalid_client";

    /// <summary>The <c>invalid_grant</c> token endpoint error (RFC 6749 §5.2).</summary>
    public const string InvalidGrant = "invalid_grant";

    /// <summary>The <c>unsupported_grant_type</c> token endpoint error (RFC 6749 §5.2).</summary>
    public const string UnsupportedGrantType = "unsupported_grant_type";

    /// <summary>The <c>interaction_required</c> error (Core §3.1.2.6).</summary>
    public const string InteractionRequired = "interaction_required";

    /// <summary>The <c>login_required</c> error (Core §3.1.2.6).</summary>
    public const string LoginRequired = "login_required";

    /// <summary>The <c>account_selection_required</c> error (Core §3.1.2.6).</summary>
    public const string AccountSelectionRequired = "account_selection_required";

    /// <summary>The <c>consent_required</c> error (Core §3.1.2.6).</summary>
    public const string ConsentRequired = "consent_required";

    /// <summary>The <c>invalid_request_uri</c> error (Core §3.1.2.6).</summary>
    public const string InvalidRequestUri = "invalid_request_uri";

    /// <summary>The <c>invalid_request_object</c> error (Core §3.1.2.6).</summary>
    public const string InvalidRequestObject = "invalid_request_object";

    /// <summary>The <c>request_not_supported</c> error (Core §3.1.2.6).</summary>
    public const string RequestNotSupported = "request_not_supported";

    /// <summary>The <c>request_uri_not_supported</c> error (Core §3.1.2.6).</summary>
    public const string RequestUriNotSupported = "request_uri_not_supported";

    /// <summary>The <c>registration_not_supported</c> error (Core §3.1.2.6).</summary>
    public const string RegistrationNotSupported = "registration_not_supported";

    /// <summary>The <c>invalid_redirect_uri</c> registration error (DCR §3.2.2).</summary>
    public const string InvalidRedirectUri = "invalid_redirect_uri";

    /// <summary>The <c>invalid_client_metadata</c> registration error (DCR §3.2.2).</summary>
    public const string InvalidClientMetadata = "invalid_client_metadata";
}
